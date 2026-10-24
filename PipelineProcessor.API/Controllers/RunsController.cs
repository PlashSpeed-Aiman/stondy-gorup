using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Jobs;
using PipelineProcessor.Models;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor.Controllers;

[ApiController]
[Route("pipelines")]
[Tags("Runs")]
public class RunsController : ControllerBase
{
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

    [HttpPost("{pipelineId}/runs")]
    public async Task<IActionResult> CreateRun(
        string pipelineId,
        [FromForm] CreateIliRunDto request,
        CancellationToken cancellationToken)
    {
        var pipelineExists = await _pipelineDbContext.Pipelines
            .AnyAsync(pipeline => pipeline.Id == pipelineId, cancellationToken);

        if (!pipelineExists)
        {
            return NotFound();
        }

        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest(new { error = "An Excel file is required." });
        }

        if (request.InspectionDate == default)
        {
            return BadRequest(new { error = "inspectionDate is required." });
        }

        await using var stream = new MemoryStream();
        await request.File.CopyToAsync(stream, cancellationToken);

        var runId = Guid.NewGuid().ToString();
        var storedKey = await _fileStorage.WriteAsync(runId, stream.ToArray(), cancellationToken);

        if (storedKey is null)
        {
            return Problem(
                "The uploaded file could not be stored.",
                statusCode: StatusCodes.Status502BadGateway);
        }

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
        await _pipelineDbContext.SaveChangesAsync(cancellationToken);

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
            _logger.LogError(exception, "Could not enqueue the import job for run {RunId}.", run.Id);

            run.Status = IliRunStatus.Failed;
            run.Error = "The import job could not be enqueued.";
            await _pipelineDbContext.SaveChangesAsync(cancellationToken);

            return Problem(
                "The import job could not be enqueued.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

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

    [HttpGet("/runs/{runId}/status", Name = "GetRunStatus")]
    public async Task<IActionResult> GetRunStatus(string runId, CancellationToken cancellationToken)
    {
        var run = await _pipelineDbContext.IliRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken);

        if (run is null)
        {
            return NotFound(new { status = "notFound" });
        }

        var status = run.Status switch
        {
            IliRunStatus.Importing or IliRunStatus.Assessing => "processing",
            IliRunStatus.Completed => "completed",
            IliRunStatus.Failed => "failed",
            _ => run.Status.ToLowerInvariant()
        };

        return Ok(new RunStatusDto
        {
            RunId = run.Id,
            Status = status,
            ProgressPct = run.ProgressPct,
            Error = run.Status == IliRunStatus.Failed ? run.Error : null
        });
    }
}
