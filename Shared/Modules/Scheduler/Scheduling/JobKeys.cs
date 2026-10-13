using Quartz;

namespace Scheduler.Scheduling;

internal static class JobKeys
{
    public const string Group = "scheduled-tasks";

    public static JobKey ForSchedule(string scheduleKey) => new(scheduleKey, Group);
}
