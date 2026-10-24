using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;
using PipelineProcessor.Services;
using Quartz;

namespace PipelineProcessor.Jobs;

// ============================================================================
// AssessmentJob
// ----------------------------------------------------------------------------
// This job turns raw anomalies into engineering results. It runs after
// IliImportJob has saved the spreadsheet rows and set the run to Assessing:
//
//   Execute
//     -> RunAsync
//          -> load the run and its pipeline (D, t, SMYS, MAOP)
//          -> create one Assessment row
//          -> for every anomaly:
//               depthMm = depth_pct / 100 * wall thickness
//               call IntegrityCalculator (pure B31G maths)
//               save FailurePressureMpa and ERF
//          -> set the run to Completed
//
// Growth rate and remaining life are milestone 4. This job only fills in the
// ERF columns, which is what the dig list and the map colouring need.
//
// Results are stored in AnomalyResults, not on the Anomaly itself. The raw
// spreadsheet values stay untouched, so a later formula change or a different
// MAOP can create a new Assessment without losing the old one.
// ============================================================================

/// <summary>
/// Calculates Modified ASME B31G results for every anomaly in one ILI run.
/// </summary>
public sealed class AssessmentJob : IJob
{
    // Save results in batches for the same reason the importer does: one
    // SaveChanges per row would mean thousands of database round trips.
    private const int BatchSize = 1000;

    // Import uses 0..90, so assessment continues from 90 and the final
    // Completed update moves the bar to 100.
    private const int StartProgressPct = 90;

    private readonly PipelineDbContext _dbContext;
    private readonly IliRunStateService _runState;
    private readonly ILogger<AssessmentJob> _logger;

    public AssessmentJob(
        PipelineDbContext dbContext,
        IliRunStateService runState,
        ILogger<AssessmentJob> logger)
    {
        _dbContext = dbContext;
        _runState = runState;
        _logger = logger;
    }

    /// <summary>
    /// Quartz entry point. It only extracts the run id and delegates to
    /// RunAsync so the workflow can be tested without a scheduler.
    /// </summary>
    public Task Execute(IJobExecutionContext context)
    {
        return RunAsync(context.MergedJobDataMap.GetString("runId"), context.CancellationToken);
    }

    /// <summary>
    /// The real unit of work. Public so integration tests can call it directly.
    /// </summary>
    public async Task RunAsync(string? runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            _logger.LogError("AssessmentJob was enqueued without a runId.");
            return;
        }

        // Kept outside the try block so the catch block can remove a partially
        // written assessment if the calculation fails halfway through.
        string? assessmentId = null;

        try
        {
            // -----------------------------------------------------------------
            // Step 1: validate the run.
            // -----------------------------------------------------------------
            var run = await _dbContext.IliRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

            if (run is null)
            {
                _logger.LogWarning("AssessmentJob could not find run {RunId}.", runId);
                return;
            }

            // A duplicate job for a finished run must not create a second
            // assessment. A failed run is left alone so its error is preserved.
            switch (run.Status)
            {
                case IliRunStatus.Completed:
                    _logger.LogInformation(
                        "Run {RunId} is already completed; skipping assessment.",
                        runId);
                    return;
                case IliRunStatus.Failed:
                    _logger.LogWarning(
                        "Run {RunId} has failed; skipping assessment.",
                        runId);
                    return;
            }

            // -----------------------------------------------------------------
            // Step 2: load the pipe properties used by the formula.
            // -----------------------------------------------------------------
            var pipeline = await _dbContext.Pipelines
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == run.PipelineId, cancellationToken);

            if (pipeline is null)
            {
                await _runState.MarkFailedAsync(
                    runId,
                    "The pipeline for this run could not be found.",
                    CancellationToken.None);
                return;
            }

            var pipe = new PipeProperties(
                pipeline.OuterDiameterMm,
                pipeline.WallThicknessMm,
                pipeline.SmysMpa,
                pipeline.MaopMpa);

            // -----------------------------------------------------------------
            // Step 3: count the anomalies before creating the assessment.
            // -----------------------------------------------------------------
            // The count is used for progress reporting, and it also lets us
            // fail cleanly when there is nothing to assess.
            var totalAnomalies = await _dbContext.Anomalies
                .AsNoTracking()
                .CountAsync(anomaly => anomaly.RunId == runId, cancellationToken);

            if (totalAnomalies == 0)
            {
                await _runState.MarkFailedAsync(
                    runId,
                    "There are no imported anomalies to assess.",
                    CancellationToken.None);
                return;
            }

            // -----------------------------------------------------------------
            // Step 4: create the Assessment header.
            // -----------------------------------------------------------------
            // MaopMpaUsed is copied now. If someone edits the pipeline later,
            // this assessment still records which pressure it actually used.
            assessmentId = Guid.NewGuid().ToString();

            _dbContext.Assessments.Add(new Assessment
            {
                Id = assessmentId,
                RunId = runId,
                Method = IntegrityCalculator.MethodName,
                MaopMpaUsed = pipeline.MaopMpa,
                CreatedAt = DateTime.UtcNow
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();

            await _runState.SetStateAsync(
                runId,
                IliRunStatus.Assessing,
                StartProgressPct,
                error: null,
                cancellationToken);

            // -----------------------------------------------------------------
            // Step 5: calculate and save results in batches.
            // -----------------------------------------------------------------
            var processed = 0;
            var repairCount = 0;
            long lastAnomalyId = 0;

            while (true)
            {
                // Read the next batch by primary key. Using "Id > lastId"
                // instead of Skip/Take keeps the query fast even for large runs.
                // Copy the value first: EF captures it when the query is built,
                // and modifying the outer variable afterwards would be confusing.
                var afterAnomalyId = lastAnomalyId;

                var batch = await _dbContext.Anomalies
                    .AsNoTracking()
                    .Where(anomaly => anomaly.RunId == runId && anomaly.Id > afterAnomalyId)
                    .OrderBy(anomaly => anomaly.Id)
                    .Take(BatchSize)
                    .Select(anomaly => new AnomalyInput(
                        anomaly.Id,
                        anomaly.DepthPct,
                        anomaly.LengthMm))
                    .ToListAsync(cancellationToken);

                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var anomaly in batch)
                {
                    // The spreadsheet stores depth as a percentage; B31G needs
                    // millimetres. Convert once, here, so the calculator always
                    // receives the units its parameters promise.
                    var depthMm = anomaly.DepthPct / 100.0 * pipe.WallThicknessMm;

                    var result = IntegrityCalculator.Calculate(pipe, depthMm, anomaly.LengthMm);

                    if (result.Erf >= 1.0)
                    {
                        repairCount++;
                    }

                    _dbContext.AnomalyResults.Add(new AnomalyResult
                    {
                        AssessmentId = assessmentId,
                        AnomalyId = anomaly.Id,
                        FailurePressureMpa = result.FailurePressureMpa,
                        Erf = result.Erf,

                        // Growth calculations belong to milestone 4.
                        MatchedAnomalyId = null,
                        GrowthMmPerYear = null,
                        RemainingLifeYears = null
                    });

                    lastAnomalyId = anomaly.Id;
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                _dbContext.ChangeTracker.Clear();

                processed += batch.Count;
                await UpdateProgressAsync(runId, processed, totalAnomalies, cancellationToken);
            }

            // -----------------------------------------------------------------
            // Step 6: finish.
            // -----------------------------------------------------------------
            // If this does not match, some anomalies disappeared while the job
            // was running. Mark the run failed rather than publish a partial
            // assessment that looks complete.
            if (processed != totalAnomalies)
            {
                throw new InvalidOperationException(
                    $"Assessed {processed} of {totalAnomalies} anomalies.");
            }

            await _runState.SetStateAsync(
                runId,
                IliRunStatus.Completed,
                progressPct: 100,
                error: null,
                cancellationToken);

            _logger.LogInformation(
                "Assessed {AnomalyCount} anomalies for run {RunId}; {RepairCount} need repair.",
                processed,
                runId,
                repairCount);
        }
        catch (Exception exception)
        {
            // -----------------------------------------------------------------
            // Failure path.
            // -----------------------------------------------------------------
            _logger.LogError(exception, "Assessing run {RunId} failed.", runId);

            // Remove the partial assessment so a failed run does not expose
            // half-calculated results.
            await TryDeleteAssessmentAsync(assessmentId);

            // Try to record the error even if the original token was cancelled.
            try
            {
                await _runState.MarkFailedAsync(runId, exception.Message, CancellationToken.None);
            }
            catch (Exception updateException)
            {
                _logger.LogError(
                    updateException,
                    "Could not record the failure for run {RunId}.",
                    runId);
            }

            throw new JobExecutionException(exception, refireImmediately: false);
        }
    }

    private async Task UpdateProgressAsync(
        string runId,
        int processed,
        int total,
        CancellationToken cancellationToken)
    {
        // Keep the public value at 99 until the Completed update. The client
        // should never see 100% while the final status write is still pending.
        var fraction = processed / (double)total;
        var progress = (int)Math.Round(StartProgressPct + fraction * (100 - StartProgressPct));
        progress = Math.Clamp(progress, StartProgressPct, 99);

        await _runState.SetStateAsync(
            runId,
            IliRunStatus.Assessing,
            progress,
            error: null,
            cancellationToken);
    }

    private async Task TryDeleteAssessmentAsync(string? assessmentId)
    {
        if (string.IsNullOrWhiteSpace(assessmentId))
        {
            return;
        }

        try
        {
            _dbContext.ChangeTracker.Clear();

            var results = await _dbContext.AnomalyResults
                .Where(result => result.AssessmentId == assessmentId)
                .ToListAsync(CancellationToken.None);

            _dbContext.AnomalyResults.RemoveRange(results);

            var assessment = await _dbContext.Assessments
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == assessmentId,
                    CancellationToken.None);

            if (assessment is not null)
            {
                _dbContext.Assessments.Remove(assessment);
            }

            await _dbContext.SaveChangesAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
        }
        catch (Exception cleanupException)
        {
            _logger.LogError(
                cleanupException,
                "Could not remove partial assessment {AssessmentId}.",
                assessmentId);
        }
    }

    /// <summary>
    /// A small projection so the batch query only loads the three columns the
    /// calculation needs instead of whole Anomaly entities.
    /// </summary>
    private sealed record AnomalyInput(long Id, double DepthPct, double LengthMm);
}
