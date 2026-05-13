using Quartz;
using Scheduler.Scheduling;
using Shared.Messaging;

namespace Scheduler.Triggers;

internal static class QuartzScheduleFactory
{
    public const string ScheduleKeyData = "scheduleKey";
    public const string TaskTypeData = "taskType";

    public static JobKey JobKeyFor(ScheduledTaskDto task) => JobKeys.ForSchedule(task.Key);

    public static JobKey JobKeyFor(string scheduleKey) => JobKeys.ForSchedule(scheduleKey);

    public static IJobDetail BuildJob(ScheduledTaskDto task)
        => JobBuilder.Create(TaskTypeRegistry.GetJobType(task.TaskType))
            .WithIdentity(JobKeyFor(task))
            .UsingJobData(ScheduleKeyData, task.Key)
            .UsingJobData(TaskTypeData, task.TaskType)
            .Build();

    public static ITrigger BuildTrigger(ScheduledTaskDto task)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(TriggerKeys.ForSchedule(task.Key))
            .ForJob(JobKeyFor(task))
            .UsingJobData(ScheduleKeyData, task.Key)
            .UsingJobData(TaskTypeData, task.TaskType);

        if (!string.IsNullOrWhiteSpace(task.Cron))
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(task.Timezone);
            return builder
                .WithCronSchedule(task.Cron, schedule => schedule
                    .InTimeZone(timezone)
                    .WithMisfireHandlingInstructionDoNothing())
                .Build();
        }

        if (task.IntervalSeconds is { } intervalSeconds)
        {
            return builder
                .StartNow()
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(TimeSpan.FromSeconds(intervalSeconds))
                    .RepeatForever()
                    .WithMisfireHandlingInstructionNextWithRemainingCount())
                .Build();
        }

        throw new SchedulerException($"Schedule '{task.Key}' has no trigger definition.");
    }
}
