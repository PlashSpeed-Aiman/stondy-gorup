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
//          -> set the run to Completed or Failed
//
// The client polls GET /runs/{runId}/status while this happens.
//
// This job deliberately does NOT calculate ERF or corrosion growth. Those
// calculations belong to the later assessment job, which keeps the maths in a
// pure, easily testable class instead of inside infrastructure code.
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
    // factory. The job never creates a DbContext or storage adapter itself.
    private readonly PipelineDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<IliImportJob> _logger;

    /// <summary>
    /// Constructor injection: the database, file storage and logger are all
    /// supplied from outside. This is also what makes the job testable: a test
    /// can pass an in-memory database and a fake storage implementation.
    /// </summary>
    public IliImportJob(
        PipelineDbContext dbContext,
        IFileStorage fileStorage,
        ILogger<IliImportJob> logger)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
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
                await MarkFailedAsync(runId, parseResult.FatalError, cancellationToken);
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

                await MarkFailedAsync(runId, message, cancellationToken);
                return;
            }

            // 3f. Save the valid rows in batches (see SaveAnomaliesAsync).
            await SaveAnomaliesAsync(runId, parseResult.Anomalies, cancellationToken);

            // 3g. Save the rejected rows. This is not a fatal error on its own:
            // a small number of bad rows is normal in vendor spreadsheets.
            await SaveImportErrorsAsync(runId, parseResult.Errors, cancellationToken);

            // 3h. The import succeeded. Set progress to 100 and clear any old
            // error message. This is the state the polling client waits for.
            await SetRunStateAsync(
                runId,
                IliRunStatus.Completed,
                progressPct: 100,
                error: null,
                cancellationToken);

            // 3i. Write a structured summary to the application log.
            _logger.LogInformation(
                "Imported {AnomalyCount} anomalies with {ErrorCount} rejected rows for run {RunId}.",
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
                await MarkFailedAsync(runId, exception.Message, CancellationToken.None);
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
        // Progress is scaled to 90%, not 100%. The last 10% is reserved for
        // saving rejected rows and writing the final Completed status, so the
        // client never sees "100%" while work is still happening.
        var progress = total == 0 ? 100 : (int)Math.Round(saved / (double)total * 90);

        await SetRunStateAsync(
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

    /// <summary>
    /// Convenience wrapper for marking a run as failed.
    /// </summary>
    private Task MarkFailedAsync(
        string runId,
        string error,
        CancellationToken cancellationToken)
    {
        // progressPct is null, so the progress value already stored is kept.
        // Only the status and the error message change.
        return SetRunStateAsync(runId, IliRunStatus.Failed, progressPct: null, error, cancellationToken);
    }

    /// <summary>
    /// The single place that changes a run's status, progress and error text.
    /// </summary>
    private async Task SetRunStateAsync(
        string runId,
        string status,
        int? progressPct,
        string? error,
        CancellationToken cancellationToken)
    {
        // The run is fetched again on every call because ChangeTracker.Clear()
        // has detached any previously loaded copy. The database is the source
        // of truth between batches.
        var run = await _dbContext.IliRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        // The run may have been deleted while the job was running. In that
        // case there is nothing to update, and no reason to crash.
        if (run is null)
        {
            return;
        }

        run.Status = status;
        run.Error = error;

        // Progress is optional. Passing null means "leave the current value
        // alone", which is what a failure wants to do.
        if (progressPct.HasValue)
        {
            run.ProgressPct = progressPct.Value;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Detach the run as well. The next call will load a fresh copy.
        _dbContext.ChangeTracker.Clear();
    }
}
