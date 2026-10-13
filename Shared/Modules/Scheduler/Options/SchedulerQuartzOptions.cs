namespace Scheduler.Options;

public sealed class SchedulerQuartzOptions
{
    public const string SectionName = "Quartz";

    public int MaxConcurrency { get; init; } = 10;
}
