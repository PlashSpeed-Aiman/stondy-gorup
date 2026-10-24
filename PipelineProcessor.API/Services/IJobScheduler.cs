using Quartz;

namespace PipelineProcessor.Services;

/// <summary>
/// A port for enqueuing background jobs without depending on a scheduler implementation.
/// </summary>
public interface IJobScheduler
{
    Task EnqueueAsync<TJob>(JobDataMap data, CancellationToken cancellationToken = default)
        where TJob : IJob;
}
