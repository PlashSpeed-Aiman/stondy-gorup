using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor.Jobs;

// ============================================================================
// IliImportJob
// ----------------------------------------------------------------------------
// This class is the background worker that turns an uploaded ILI spreadsheet
// into rows in the database. It runs after RunsController has already:
//
//   1. stored the .xlsx file under a new run id, and
//   2. inserted an IliRun row with Status = Importing.
//
// The overall flow is:
//
//   Execute
//     -> RunAsync
//          -> read the file from IFileStorage
//          -> IliRowParser.Parse (pure: bytes in, rows out)
//          -> save valid rows in batches of 1,000
//          -> save rejected rows to ImportErrors
//          -> set the run to Assessing
//          -> enqueue AssessmentJob to calculate ERF
//
// The client polls GET /runs/{runId}/status while this happens.
//
// This job deliberately does NOT calculate ERF. That maths belongs to
// AssessmentJob and IntegrityCalculator, which keeps it pure and testable
// instead of mixing it into spreadsheet and database code.
// ============================================================================

/// <summary>
/// Imports one ILI spreadsheet into the database. The run id is also the key
/// used by <see cref="IFileStorage"/> to locate the uploaded file.
/// </summary>
[DisallowConcurrentExecution]
public sealed class IliImportJob : IJob
{
    // BatchSize controls how many anomaly rows are sent to the database in one
    // SaveChanges call. Saving 50,000 rows one at a time would mean 50,000
    // round trips; batching turns that into about 50 round trips.
    private const int BatchSize = 1000;

    // If more than 10% of the data rows are invalid, the file is treated as
    // unreliable and the whole run fails. A few bad rows are tolerated and
    // recorded individually in ImportErrors instead.
    private const double MaxErrorRatio = 0.10;

    // These dependencies are injected by Quartz's dependency-injection job
    // factory. The job never creates a DbContext, storage adapter or scheduler
    // itself.
    private readonly PipelineDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IJobScheduler _jobScheduler;
    private readonly IliRunStateService _runState;
    private readonly ILogger<IliImportJob> _logger;

    /// <summary>
    /// Constructor injection: every dependency is supplied from outside. This
    /// is also what makes the job testable: a test can pass an in-memory
    /// database, a fake storage implementation and a recording job scheduler.
    /// </summary>
    public IliImportJob(
        PipelineDbContext dbContext,
        IFileStorage fileStorage,
        IJobScheduler jobScheduler,
        IliRunStateService runState,
        ILogger<IliImportJob> logger)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _jobScheduler = jobScheduler;
        _runState = runState;
        _logger = logger;
    }

    /// <summary>
    /// Quartz calls this method when the job fires.
    /// </summary>
    public Task Execute(IJobExecutionContext context)
    {
        // Step 1: read the run id that RunsController attached when it
        // scheduled the job. MergedJobDataMap is Quartz's job-data dictionary.
        //
        // Step 2: pass Quartz's cancellation token down the call chain. It is
        // cancelled if the scheduler shuts down while the job is still running.
        //
        // Step 3: hand off to RunAsync. Keeping Execute this small means the
        // real logic can be tested without starting a Quartz scheduler at all.
        return RunAsync(context.MergedJobDataMap.GetString("runId"), context.CancellationToken);
    }

    /// <summary>
    /// The real unit of work: read, parse, save and update the run status.
    /// Public so integration tests can call it directly.
    /// </summary>
    public async Task RunAsync(string? runId, CancellationToken cancellationToken = default)
    {
        // --------------------------------------------------------------------
        // Step 1: validate the run id.
        // --------------------------------------------------------------------
        // A missing id means the job was scheduled incorrectly. There is no
        // sensible work to do, so log the problem and stop.
        if (string.IsNullOrWhiteSpace(runId))
        {
            _logger.LogError("IliImportJob was enqueued without a runId.");
            return;
        }

        // --------------------------------------------------------------------
        // Step 2: make sure the run still exists.
        // --------------------------------------------------------------------
        // The run could have been deleted between scheduling and execution.
        // AnyAsync becomes a lightweight "SELECT EXISTS" query, so the
        // database does the check instead of loading the whole row.
        if (!await _dbContext.IliRuns.AnyAsync(run => run.Id == runId, cancellationToken))
        {
            _logger.LogWarning("IliImportJob could not find run {RunId}.", runId);
            return;
        }

        // --------------------------------------------------------------------
        // Step 3: do the import.
        // --------------------------------------------------------------------
        // Everything is inside one try/catch so that any unexpected failure
        // (storage error, corrupt file, database error) can be recorded on the
        // run before the exception is passed back to Quartz.
        try
        {
            // 3a. Read the original spreadsheet bytes. The run id doubles as
            // the storage key, which is why the job only needs one argument.
            var fileBytes = await _fileStorage.ReadAsync(runId, cancellationToken);

            // 3b. Parse the file. IliRowParser is a pure function: it touches
            // no database, network or clock. It returns three things:
            //   - Anomalies : rows that passed validation
            //   - Errors    : rows that failed, each with its row number
            //   - FatalError: a problem that makes the whole file unusable
            //                 (for example, missing required columns)
            var parseResult = IliRowParser.Parse(fileBytes);

            // 3c. A fatal problem means no row can be trusted. Mark the run as
            // failed and stop. The client will receive this message from the
            // status endpoint.
            if (parseResult.FatalError is not null)
            {
                await _runState.MarkFailedAsync(runId, parseResult.FatalError, cancellationToken);
                return;
            }

            // 3d. Work out how reliable the file is. The denominator includes
            // both good and bad rows, so the ratio is "bad rows / all rows".
            var totalRows = parseResult.Anomalies.Count + parseResult.Errors.Count;
            var errorRatio = totalRows == 0 ? 0 : parseResult.Errors.Count / (double)totalRows;

            // 3e. Fail the run when:
            //       - there is not a single valid row, or
            //       - more than 10% of the rows are invalid.
            // The rejected rows are still saved first so that the failure can
            // be investigated row by row.
            if (parseResult.Anomalies.Count == 0 || errorRatio > MaxErrorRatio)
            {
                await SaveImportErrorsAsync(runId, parseResult.Errors, cancellationToken);

                var message = parseResult.Anomalies.Count == 0
                    ? "No valid rows were found in the file."
                    : $"More than {MaxErrorRatio:P0} of the rows were invalid.";

                await _runState.MarkFailedAsync(runId, message, cancellationToken);
                return;
            }

            // 3f. Save the valid rows in batches (see SaveAnomaliesAsync).
            await SaveAnomaliesAsync(runId, parseResult.Anomalies, cancellationToken);

            // 3g. Save the rejected rows. This is not a fatal error on its own:
            // a small number of bad rows is normal in vendor spreadsheets.
            await SaveImportErrorsAsync(runId, parseResult.Errors, cancellationToken);

            // 3h. The import worked, but the run is not finished yet. ERF has
            // not been calculated, so move to Assessing and leave progress at
            // 90%. AssessmentJob will take it to 100 and then Completed.
            await _runState.SetStateAsync(
                runId,
                IliRunStatus.Assessing,
                progressPct: 90,
                error: null,
                cancellationToken);

            // 3i. Hand the next step to a separate job. This is the same
            // IJobScheduler port the controller uses: the import job does not
            // know that Quartz is underneath.
            try
            {
                await _jobScheduler.EnqueueAsync<AssessmentJob>(
                    new JobDataMap { ["runId"] = runId },
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // Without this job the run would sit in Assessing forever.
                _logger.LogError(
                    exception,
                    "Could not enqueue the assessment job for run {RunId}.",
                    runId);

                await _runState.MarkFailedAsync(
                    runId,
                    "The assessment job could not be enqueued.",
                    CancellationToken.None);

                return;
            }

            // 3j. Write a structured summary to the application log.
            _logger.LogInformation(
                "Imported {AnomalyCount} anomalies with {ErrorCount} rejected rows for run {RunId}; assessment enqueued.",
                parseResult.Anomalies.Count,
                parseResult.Errors.Count,
                runId);
        }
        catch (Exception exception)
        {
            // -----------------------------------------------------------------
            // Step 4: record the failure, then tell Quartz the job failed.
            // -----------------------------------------------------------------
            _logger.LogError(exception, "Importing run {RunId} failed.", runId);

            // Try to persist the error message. CancellationToken.None is used
            // on purpose: if the original token was already cancelled, we still
            // want the database update to be attempted.
            try
            {
                await _runState.MarkFailedAsync(runId, exception.Message, CancellationToken.None);
            }
            catch (Exception updateException)
            {
                // The original problem is more important than this secondary
                // one, so log the second failure but keep the first exception.
                _logger.LogError(updateException, "Could not record the failure for run {RunId}.", runId);
            }

            // Rethrowing as JobExecutionException lets Quartz mark the job as
            // failed and show it in its logs. refireImmediately: false prevents
            // an immediate retry loop for a permanently broken file.
            throw new JobExecutionException(exception, refireImmediately: false);
        }
    }

    /// <summary>
    /// Saves valid anomalies in batches of <see cref="BatchSize"/>.
    /// </summary>
    private async Task SaveAnomaliesAsync(
        string runId,
        IReadOnlyList<ParsedAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        // Reuse one list for every batch. The capacity hint avoids repeated
        // internal array growth while the batch fills up.
        var batch = new List<Anomaly>(BatchSize);
        var saved = 0;

        foreach (var parsed in anomalies)
        {
            // Convert the parser's simple record into a database entity.
            // RunId links every anomaly back to the IliRun that produced it.
            batch.Add(new Anomaly
            {
                RunId = runId,
                RowNumber = parsed.RowNumber,
                VendorFeatureId = parsed.VendorFeatureId,
                DistanceM = parsed.DistanceM,
                Lat = parsed.Lat,
                Lon = parsed.Lon,
                DepthPct = parsed.DepthPct,
                LengthMm = parsed.LengthMm
            });

            // Keep collecting until the batch is full. The "continue" is just
            // an early skip; it does not end the loop.
            if (batch.Count < BatchSize)
            {
                continue;
            }

            // The batch is full: write it, then report progress.
            saved += await SaveBatchAsync(batch, cancellationToken);
            await UpdateProgressAsync(runId, saved, anomalies.Count, cancellationToken);
        }

        // The last batch is usually smaller than 1,000, so it never triggered
        // the save inside the loop. Save any leftovers here.
        if (batch.Count > 0)
        {
            saved += await SaveBatchAsync(batch, cancellationToken);
            await UpdateProgressAsync(runId, saved, anomalies.Count, cancellationToken);
        }
    }

    /// <summary>
    /// Writes one batch to the database and clears EF Core's change tracker.
    /// Returns the number of rows saved so the caller can report progress.
    /// </summary>
    private async Task<int> SaveBatchAsync(
        List<Anomaly> batch,
        CancellationToken cancellationToken)
    {
        // AddRange marks every entity in the list as "Added".
        _dbContext.Anomalies.AddRange(batch);

        // SaveChanges sends the INSERT statements to MySQL.
        await _dbContext.SaveChangesAsync(cancellationToken);

        // EF Core keeps every saved entity in memory so it can detect future
        // changes. After tens of thousands of rows that tracker becomes large
        // and slow. Clearing it says: "the data is safely saved; stop tracking
        // these objects." This is the key to importing large files efficiently.
        _dbContext.ChangeTracker.Clear();

        // Capture the count before emptying the list, then reuse the same list
        // for the next batch.
        var count = batch.Count;
        batch.Clear();

        return count;
    }

    /// <summary>
    /// Stores a progress percentage on the run while the import is running.
    /// </summary>
    private async Task UpdateProgressAsync(
        string runId,
        int saved,
        int total,
        CancellationToken cancellationToken)
    {
        // Import progress stops at 90%. The final 10% belongs to AssessmentJob,
        // so the client never sees 100% while ERF is still being calculated.
        var progress = total == 0 ? 90 : (int)Math.Round(saved / (double)total * 90);

        await _runState.SetStateAsync(
            runId,
            IliRunStatus.Importing,
            progress,
            error: null,
            cancellationToken);
    }

    /// <summary>
    /// Stores rejected rows in the ImportErrors table, in chunks.
    /// </summary>
    private async Task SaveImportErrorsAsync(
        string runId,
        IReadOnlyList<IliRowError> errors,
        CancellationToken cancellationToken)
    {
        // Chunk splits the error list into groups of at most 1,000 items.
        // This keeps memory use and SQL statement size under control.
        foreach (var chunk in errors.Chunk(BatchSize))
        {
            // One ImportError per rejected row. The row number lets an engineer
            // open the original spreadsheet and find the exact problem.
            _dbContext.ImportErrors.AddRange(chunk.Select(error => new ImportError
            {
                RunId = runId,
                RowNumber = error.RowNumber,
                Message = error.Message
            }));

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Same reasoning as SaveBatchAsync: detach the saved entities.
            _dbContext.ChangeTracker.Clear();
        }
    }
}
