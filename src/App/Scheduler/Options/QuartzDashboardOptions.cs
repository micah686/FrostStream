namespace Scheduler.Options;

public sealed class QuartzDashboardOptions
{
    public const string SectionName = "QuartzDashboard";

    public string Path { get; init; } = "/quartz";

    public bool ReadOnly { get; init; } = false;

    public bool LazyInit { get; init; } = false;

    public int TimelineSpanMinutes { get; init; } = 60;
}
