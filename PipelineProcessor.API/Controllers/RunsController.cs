using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Jobs;
using PipelineProcessor.Models;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor.Controllers;

// ============================================================================
// RunsController
// ----------------------------------------------------------------------------
// This controller owns the "upload an ILI spreadsheet" use case:
//
//   POST /pipelines/{pipelineId}/runs  -> start an import
//   GET  /runs/{runId}/status          -> poll that import
//
// It is deliberately thin. It validates the request, stores the file, creates
// a database row, and enqueues a background job. The heavy work (reading the
// spreadsheet and inserting thousands of rows) happens in IliImportJob, so the
// HTTP response returns immediately.
// ============================================================================

[ApiController]
[Route("pipelines")]
[Tags("Runs")]
public class RunsController : ControllerBase
{
    // Dependencies are injected through the constructor:
    //   _pipelineDbContext -> reads pipelines and reads/writes runs
    //   _fileStorage       -> stores the uploaded .xlsx (local disk or R2)
    //   _jobScheduler      -> enqueues the Quartz import job
    //   _logger            -> reports problems that happen during the request
    private readonly PipelineDbContext _pipelineDbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IJobScheduler _jobScheduler;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        PipelineDbContext pipelineDbContext,
        IFileStorage fileStorage,
        IJobScheduler jobScheduler,
        ILogger<RunsController> logger)
    {
        _pipelineDbContext = pipelineDbContext;
        _fileStorage = fileStorage;
        _jobScheduler = jobScheduler;
        _logger = logger;
    }

    /// <summary>
    /// Accepts a multipart upload containing an inspection file and its date.
    /// Returns immediately with the new run id and the URL used to poll it.
    /// </summary>
    [HttpPost("{pipelineId}/runs")]
    public async Task<IActionResult> CreateRun(
        string pipelineId,
        [FromForm] CreateIliRunDto request,
        CancellationToken cancellationToken)
    {
        // --------------------------------------------------------------------
        // Step 1: make sure the pipeline exists.
        // --------------------------------------------------------------------
        // Uploading a run to an unknown pipeline would create an orphan row
        // (and MySQL's foreign key would reject it later anyway). Checking
        // first lets us return a clear 404 without storing anything.
        var pipelineExists = await _pipelineDbContext.Pipelines
            .AnyAsync(pipeline => pipeline.Id == pipelineId, cancellationToken);

        if (!pipelineExists)
        {
            return NotFound();
        }

        // --------------------------------------------------------------------
        // Step 2: validate the uploaded file.
        // --------------------------------------------------------------------
        // A missing or empty file cannot be imported. This is a client error,
        // so 400 Bad Request is the right response.
        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest(new { error = "An Excel file is required." });
        }

        // --------------------------------------------------------------------
        // Step 3: validate the inspection date.
        // --------------------------------------------------------------------
        // DateOnly's default value is 0001-01-01, which means the client did
        // not send a real date. Every run needs a date so later runs can be
        // ordered and compared for corrosion growth.
        if (request.InspectionDate == default)
        {
            return BadRequest(new { error = "inspectionDate is required." });
        }

        // --------------------------------------------------------------------
        // Step 4: copy the upload into memory.
        // --------------------------------------------------------------------
        // IFormFile is a stream owned by ASP.NET Core. Reading it into a byte
        // array lets us pass the same bytes to the storage adapter.
        await using var stream = new MemoryStream();
        await request.File.CopyToAsync(stream, cancellationToken);

        // --------------------------------------------------------------------
        // Step 5: create the run id and store the file.
        // --------------------------------------------------------------------
        // The run id is used twice on purpose:
        //   - as the primary key of the IliRun row
        //   - as the storage key of the uploaded file
        // That gives the background job a single value to look up both.
        var runId = Guid.NewGuid().ToString();
        var storedKey = await _fileStorage.WriteAsync(runId, stream.ToArray(), cancellationToken);

        // A null key means the storage adapter could not save the file.
        // 502 Bad Gateway is appropriate because an upstream dependency failed.
        if (storedKey is null)
        {
            return Problem(
                "The uploaded file could not be stored.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // --------------------------------------------------------------------
        // Step 6: create the database record for the run.
        // --------------------------------------------------------------------
        // Status starts as Importing. Progress starts at 0. CreatedAt records
        // when the client asked for the import, independent of the inspection
        // date that the file describes.
        var run = new IliRun
        {
            Id = runId,
            PipelineId = pipelineId,
            InspectionDate = request.InspectionDate,
            Status = IliRunStatus.Importing,
            ProgressPct = 0,
            CreatedAt = DateTime.UtcNow
        };

        _pipelineDbContext.IliRuns.Add(run);

        // Save before scheduling the job. The job queries the run by id, so the
        // row must exist in the database before the job can start.
        await _pipelineDbContext.SaveChangesAsync(cancellationToken);

        // --------------------------------------------------------------------
        // Step 7: enqueue the background job.
        // --------------------------------------------------------------------
        // The job data map is how values reach the job. IliImportJob's Execute
        // reads "runId" from it.
        try
        {
            var data = new JobDataMap
            {
                ["runId"] = run.Id
            };

            await _jobScheduler.EnqueueAsync<IliImportJob>(data, cancellationToken);
        }
        catch (Exception exception)
        {
            // If scheduling fails, no worker will ever process the run. Mark it
            // Failed right away so the client's status polling does not stay
            // on "processing" forever, then report the server error.
            _logger.LogError(exception, "Could not enqueue the import job for run {RunId}.", run.Id);

            run.Status = IliRunStatus.Failed;
            run.Error = "The import job could not be enqueued.";
            await _pipelineDbContext.SaveChangesAsync(cancellationToken);

            return Problem(
                "The import job could not be enqueued.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // --------------------------------------------------------------------
        // Step 8: return 201 Created with the polling URL.
        // --------------------------------------------------------------------
        // The client does not wait for the import. It receives the run id and a
        // relative URL it can call until the status becomes completed/failed.
        //
        // Url.RouteUrl uses the named route on GetRunStatus below, so the URL
        // stays correct even if the route template changes later.
        var statusUrl = Url.RouteUrl("GetRunStatus", new { runId = run.Id }) ?? string.Empty;

        return CreatedAtRoute(
            "GetRunStatus",
            new { runId = run.Id },
            new RunCreatedDto
            {
                RunId = run.Id,
                Status = run.Status,
                StatusUrl = statusUrl
            });
    }

    /// <summary>
    /// Polling endpoint: tells the client whether the import is still running,
    /// finished, or failed.
    /// </summary>
    [HttpGet("/runs/{runId}/status", Name = "GetRunStatus")]
    public async Task<IActionResult> GetRunStatus(string runId, CancellationToken cancellationToken)
    {
        // AsNoTracking is used because this data is only read and serialized.
        // EF Core does not need to keep a tracked copy for change detection.
        var run = await _pipelineDbContext.IliRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken);

        // An unknown run id is a client error.
        if (run is null)
        {
            return NotFound(new { status = "notFound" });
        }

        // The database stores the real workflow states (Importing, Assessing,
        // Completed, Failed). The public API exposes simpler polling states.
        // Both Importing and Assessing mean "still working" to the client.
        var status = run.Status switch
        {
            IliRunStatus.Importing or IliRunStatus.Assessing => "processing",
            IliRunStatus.Completed => "completed",
            IliRunStatus.Failed => "failed",
            _ => run.Status.ToLowerInvariant()
        };

        // Only expose the error text when the run actually failed. Progress is
        // always safe to return and lets a UI show a progress bar.
        return Ok(new RunStatusDto
        {
            RunId = run.Id,
            Status = status,
            ProgressPct = run.ProgressPct,
            Error = run.Status == IliRunStatus.Failed ? run.Error : null
        });
    }
}
