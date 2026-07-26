using Conduit.NATS;
using NodaTime;
using Quartz;
using Shared.Messaging;

namespace Scheduler.Scheduling;

/// <summary>
/// Announces every schedule firing on <see cref="BackgroundRunSubjects.Dispatched"/> so a row shows
/// up on Jobs &gt; Background the instant a task goes off, rather than only once the service that
/// executes it picks the request off JetStream. Attaching this as a Quartz job listener rather than
/// wiring it into each task keeps the guarantee automatic for task types added later.
///
/// The run id is derived from the same idempotency key the published request carries, so the
/// executing service's start/progress/completion events land on this row instead of a second one.
/// </summary>
internal sealed class BackgroundRunDispatchListener(
    IMessageBus messageBus,
    IClock clock,
    ILogger<BackgroundRunDispatchListener> logger) : IJobListener
{
    private const string SchedulerOrigin = "scheduler";

    public string Name => "background-run-dispatch";

    public async Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (Describe(context) is not { } job)
            return;

        await PublishAsync(BackgroundRunSubjects.Dispatched, new BackgroundRunDispatched
        {
            RunId = BackgroundRunIds.ForIdempotencyKey(job.IdempotencyKey),
            TaskType = job.TaskType,
            ScheduleKey = job.ScheduleKey,
            Trigger = BackgroundRunTrigger.Scheduled,
            IdempotencyKey = job.IdempotencyKey,
            Origin = SchedulerOrigin,
            DueWindowUtc = job.DueWindowUtc,
            DispatchedAt = clock.GetCurrentInstant()
        }, cancellationToken);
    }

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => CloseOutAsync(context, "The scheduler vetoed this firing before it ran.", cancellationToken);

    public Task JobWasExecuted(
        IJobExecutionContext context,
        JobExecutionException? jobException,
        CancellationToken cancellationToken = default)
        // Only failures are closed out here: on success the request is on the bus and the executing
        // service owns the run from this point, reporting its own progress and completion.
        => jobException is null
            ? Task.CompletedTask
            : CloseOutAsync(context, $"The schedule could not be dispatched: {jobException.Message}", cancellationToken);

    /// <summary>
    /// Fails the queued row when the firing never made it onto the bus, so it does not sit as
    /// "queued" forever waiting for a start event that no service will ever send.
    /// </summary>
    private async Task CloseOutAsync(IJobExecutionContext context, string error, CancellationToken cancellationToken)
    {
        if (Describe(context) is not { } job)
            return;

        await PublishAsync(BackgroundRunSubjects.Completed, new BackgroundRunCompleted
        {
            RunId = BackgroundRunIds.ForIdempotencyKey(job.IdempotencyKey),
            Success = false,
            ErrorMessage = error,
            CompletedAt = clock.GetCurrentInstant()
        }, cancellationToken);
    }

    private ScheduledJobContext? Describe(IJobExecutionContext context)
    {
        try
        {
            return ScheduledJobContextFactory.Create(context, clock);
        }
        catch (JobExecutionException ex)
        {
            // The job itself raises the same failure a moment later; a listener that threw here
            // would only obscure it.
            logger.LogWarning(ex, "Skipping background run dispatch telemetry for job {JobKey}.", context.JobDetail.Key);
            return null;
        }
    }

    private async Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken)
    {
        try
        {
            await messageBus.PublishAsync(subject, message, cancellationToken);
        }
        catch (Exception ex)
        {
            // Telemetry only: a publish hiccup must never stop the schedule it is describing.
            logger.LogWarning(ex, "Failed publishing background run telemetry to {Subject}.", subject);
        }
    }
}
