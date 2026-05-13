using NodaTime;
using Scheduler.Messaging;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

public sealed class SearchReindexScheduler(INatsMessagePublisher publisher, IClock clock) : ISearchReindexScheduler
{
    public Task QueueReindexAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var message = BackgroundJobRequestFactory.CreateSearchReindex(
            context.ScheduleKey,
            context.TaskType,
            context.DueWindowUtc,
            clock.GetCurrentInstant());

        return publisher.PublishAsync(
            BackgroundJobSubjects.SearchReindexRequest,
            message,
            message.IdempotencyKey,
            cancellationToken: cancellationToken);
    }
}
