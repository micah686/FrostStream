using Quartz;

namespace Scheduler.Scheduling;

internal static class TriggerKeys
{
    public const string Group = "scheduled-task-triggers";

    public static TriggerKey ForSchedule(string scheduleKey) => new(scheduleKey, Group);
}
