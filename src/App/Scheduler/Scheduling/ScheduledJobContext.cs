using NodaTime;
using Quartz;
using Scheduler.Triggers;

namespace Scheduler.Scheduling;

public sealed record ScheduledJobContext(
    string ScheduleKey,
    string TaskType,
    Instant DueWindowUtc,
    string IdempotencyKey);

internal static class ScheduledJobContextFactory
{
    public static ScheduledJobContext Create(IJobExecutionContext context, IClock clock)
    {
        var scheduleKey = context.MergedJobDataMap.GetString(QuartzScheduleFactory.ScheduleKeyData);
        var taskType = context.MergedJobDataMap.GetString(QuartzScheduleFactory.TaskTypeData);

        if (string.IsNullOrWhiteSpace(scheduleKey) || string.IsNullOrWhiteSpace(taskType))
        {
            throw new JobExecutionException("Quartz job data did not include schedule key and task type.");
        }

        var dueWindow = context.ScheduledFireTimeUtc is { } scheduledFireTime
            ? Instant.FromDateTimeOffset(scheduledFireTime.ToUniversalTime())
            : clock.GetCurrentInstant();
        var idempotencyKey = $"{taskType}:{scheduleKey}:{dueWindow:uuuu-MM-ddTHH:mm:ss'Z'}";

        return new ScheduledJobContext(scheduleKey, taskType, dueWindow, idempotencyKey);
    }
}
