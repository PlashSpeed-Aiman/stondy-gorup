using Quartz;

namespace PipelineProcessor.Services;

// ============================================================================
// QuartzJobScheduler
// ----------------------------------------------------------------------------
// This class is the adapter between our application and Quartz.NET.
//
// Controllers depend on the IJobScheduler port, not on Quartz directly:
//
//   RunsController -> IJobScheduler -> QuartzJobScheduler -> Quartz
//
// That keeps the controller easy to test (a fake IJobScheduler can record what
// was enqueued) and means the scheduling library could be swapped later without
// changing the HTTP layer.
// ============================================================================

/// <summary>
/// Schedules an immediate one-off Quartz job.
/// </summary>
public sealed class QuartzJobScheduler : IJobScheduler
{
    // Jobs and triggers are placed in a named group. Quartz groups make it
    // easier to find, pause or remove related jobs in the scheduler.
    private const string GroupName = "pipeline-jobs";

    // ISchedulerFactory is the Quartz service that gives access to the shared
    // scheduler instance managed by the Quartz hosted service.
    private readonly ISchedulerFactory _schedulerFactory;

    public QuartzJobScheduler(ISchedulerFactory schedulerFactory)
    {
        _schedulerFactory = schedulerFactory;
    }

    /// <summary>
    /// Creates a job, attaches the supplied data, and schedules it to run now.
    /// </summary>
    /// <typeparam name="TJob">The Quartz job class to execute.</typeparam>
    public async Task EnqueueAsync<TJob>(
        JobDataMap data,
        CancellationToken cancellationToken = default)
        where TJob : IJob
    {
        // Step 1: get the scheduler that the Quartz hosted service started when
        // the application booted. The factory returns the same shared instance
        // on every call.
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken);

        // Step 2: give this particular execution a unique identity. The job
        // class name alone would collide if the same job type is enqueued
        // twice (for example, two uploaded spreadsheet runs), so a GUID makes
        // every scheduled job definition unique.
        var identity = $"{typeof(TJob).Name}-{Guid.NewGuid():N}";

        // Step 3: describe the job.
        //   Create<TJob>()  -> the class Quartz will instantiate through DI
        //   WithIdentity()  -> unique name + group in the scheduler
        //   UsingJobData()  -> data available to the job as JobDataMap
        //   Build()         -> creates the immutable job description
        //
        // For IliImportJob the data map contains "runId", which Execute reads.
        var job = JobBuilder.Create<TJob>()
            .WithIdentity(identity, GroupName)
            .UsingJobData(data)
            .Build();

        // Step 4: describe when to run it. StartNow() means "as soon as a
        // Quartz worker is free", so this is a one-off background job rather
        // than a recurring schedule. The trigger also needs a unique identity.
        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{identity}-trigger", GroupName)
            .StartNow()
            .Build();

        // Step 5: register the job and trigger with the scheduler. This method
        // returns once they are stored; the actual work runs later on a Quartz
        // worker thread, so the HTTP request is not kept waiting.
        await scheduler.ScheduleJob(job, trigger, cancellationToken);
    }
}
