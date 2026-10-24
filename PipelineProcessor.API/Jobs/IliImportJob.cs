using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor.Jobs;

[DisallowConcurrentExecution]
public sealed class IliImportJob : IJob
{
    private const int BatchSize = 1000;
    private const double MaxErrorRatio = 0.10;

    private readonly PipelineDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<IliImportJob> _logger;

    public IliImportJob(
        PipelineDbContext dbContext,
        IFileStorage fileStorage,
        ILogger<IliImportJob> logger)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context)
    {
        return RunAsync(context.MergedJobDataMap.GetString("runId"), context.CancellationToken);
    }

    public async Task RunAsync(string? runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            _logger.LogError("IliImportJob was enqueued without a runId.");
            return;
        }

        if (!await _dbContext.IliRuns.AnyAsync(run => run.Id == runId, cancellationToken))
        {
            _logger.LogWarning("IliImportJob could not find run {RunId}.", runId);
            return;
        }

        try
        {
            var fileBytes = await _fileStorage.ReadAsync(runId, cancellationToken);
            var parseResult = IliRowParser.Parse(fileBytes);

            if (parseResult.FatalError is not null)
            {
                await MarkFailedAsync(runId, parseResult.FatalError, cancellationToken);
                return;
            }

            var totalRows = parseResult.Anomalies.Count + parseResult.Errors.Count;
            var errorRatio = totalRows == 0 ? 0 : parseResult.Errors.Count / (double)totalRows;

            if (parseResult.Anomalies.Count == 0 || errorRatio > MaxErrorRatio)
            {
                await SaveImportErrorsAsync(runId, parseResult.Errors, cancellationToken);

                var message = parseResult.Anomalies.Count == 0
                    ? "No valid rows were found in the file."
                    : $"More than {MaxErrorRatio:P0} of the rows were invalid.";

                await MarkFailedAsync(runId, message, cancellationToken);
                return;
            }

            await SaveAnomaliesAsync(runId, parseResult.Anomalies, cancellationToken);
            await SaveImportErrorsAsync(runId, parseResult.Errors, cancellationToken);

            await SetRunStateAsync(
                runId,
                IliRunStatus.Completed,
                progressPct: 100,
                error: null,
                cancellationToken);

            _logger.LogInformation(
                "Imported {AnomalyCount} anomalies with {ErrorCount} rejected rows for run {RunId}.",
                parseResult.Anomalies.Count,
                parseResult.Errors.Count,
                runId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Importing run {RunId} failed.", runId);

            try
            {
                await MarkFailedAsync(runId, exception.Message, CancellationToken.None);
            }
            catch (Exception updateException)
            {
                _logger.LogError(updateException, "Could not record the failure for run {RunId}.", runId);
            }

            throw new JobExecutionException(exception, refireImmediately: false);
        }
    }

    private async Task SaveAnomaliesAsync(
        string runId,
        IReadOnlyList<ParsedAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var batch = new List<Anomaly>(BatchSize);
        var saved = 0;

        foreach (var parsed in anomalies)
        {
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

            if (batch.Count < BatchSize)
            {
                continue;
            }

            saved += await SaveBatchAsync(batch, cancellationToken);
            await UpdateProgressAsync(runId, saved, anomalies.Count, cancellationToken);
        }

        if (batch.Count > 0)
        {
            saved += await SaveBatchAsync(batch, cancellationToken);
            await UpdateProgressAsync(runId, saved, anomalies.Count, cancellationToken);
        }
    }

    private async Task<int> SaveBatchAsync(
        List<Anomaly> batch,
        CancellationToken cancellationToken)
    {
        _dbContext.Anomalies.AddRange(batch);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();

        var count = batch.Count;
        batch.Clear();

        return count;
    }

    private async Task UpdateProgressAsync(
        string runId,
        int saved,
        int total,
        CancellationToken cancellationToken)
    {
        var progress = total == 0 ? 100 : (int)Math.Round(saved / (double)total * 90);

        await SetRunStateAsync(
            runId,
            IliRunStatus.Importing,
            progress,
            error: null,
            cancellationToken);
    }

    private async Task SaveImportErrorsAsync(
        string runId,
        IReadOnlyList<IliRowError> errors,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in errors.Chunk(BatchSize))
        {
            _dbContext.ImportErrors.AddRange(chunk.Select(error => new ImportError
            {
                RunId = runId,
                RowNumber = error.RowNumber,
                Message = error.Message
            }));

            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
        }
    }

    private Task MarkFailedAsync(
        string runId,
        string error,
        CancellationToken cancellationToken)
    {
        return SetRunStateAsync(runId, IliRunStatus.Failed, progressPct: null, error, cancellationToken);
    }

    private async Task SetRunStateAsync(
        string runId,
        string status,
        int? progressPct,
        string? error,
        CancellationToken cancellationToken)
    {
        var run = await _dbContext.IliRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        if (run is null)
        {
            return;
        }

        run.Status = status;
        run.Error = error;

        if (progressPct.HasValue)
        {
            run.ProgressPct = progressPct.Value;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();
    }
}
