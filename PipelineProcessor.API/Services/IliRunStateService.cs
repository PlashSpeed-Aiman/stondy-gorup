using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;

namespace PipelineProcessor.Services;

/// <summary>
/// The single place that changes an ILI run's status, progress and error text.
/// Both the import job and the assessment job use it, so the state-update rules
/// cannot drift apart.
/// </summary>
public sealed class IliRunStateService
{
    private readonly PipelineDbContext _dbContext;

    public IliRunStateService(PipelineDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Updates the run and saves immediately.
    /// </summary>
    /// <param name="runId">The run to update.</param>
    /// <param name="status">One of the <see cref="IliRunStatus"/> values.</param>
    /// <param name="progressPct">
    /// New progress value, or null to keep the current value. Values outside
    /// 0..100 are clamped.
    /// </param>
    /// <param name="error">Error text, or null to clear an old error.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    public async Task SetStateAsync(
        string runId,
        string status,
        int? progressPct,
        string? error,
        CancellationToken cancellationToken)
    {
        // The run is re-read on every update because the jobs clear EF Core's
        // change tracker after each batch. The database is the source of truth.
        var run = await _dbContext.IliRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        // The run may have been deleted while the job was running. There is
        // nothing to update in that case, and no reason to crash.
        if (run is null)
        {
            return;
        }

        run.Status = status;
        run.Error = error;

        if (progressPct.HasValue)
        {
            run.ProgressPct = Math.Clamp(progressPct.Value, 0, 100);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Detach the run so the next batch starts from a clean change tracker.
        _dbContext.ChangeTracker.Clear();
    }

    /// <summary>
    /// Marks a run as failed while keeping its current progress value.
    /// </summary>
    public Task MarkFailedAsync(
        string runId,
        string error,
        CancellationToken cancellationToken)
    {
        return SetStateAsync(
            runId,
            IliRunStatus.Failed,
            progressPct: null,
            error,
            cancellationToken);
    }
}
