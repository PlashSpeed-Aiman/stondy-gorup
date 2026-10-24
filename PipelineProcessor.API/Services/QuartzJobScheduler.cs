using Quartz;

namespace PipelineProcessor.Services;

public sealed class QuartzJobScheduler : IJobScheduler
{
    private const string GroupName = "pipeline-jobs";

    private readonly ISchedulerFactory _schedulerFactory;

    public QuartzJobScheduler(ISchedulerFactory schedulerFactory)
    {
        _schedulerFactory = schedulerFactory;
    }

    public async Task EnqueueAsync<TJob>(
        JobDataMap data,
        CancellationToken cancellationToken = default)
        where TJob : IJob
    {
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken);
        var identity = $"{typeof(TJob).Name}-{Guid.NewGuid():N}";

        var job = JobBuilder.Create<TJob>()
            .WithIdentity(identity, GroupName)
            .UsingJobData(data)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{identity}-trigger", GroupName)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger, cancellationToken);
    }
}
