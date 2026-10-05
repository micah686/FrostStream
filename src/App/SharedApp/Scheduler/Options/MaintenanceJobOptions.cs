namespace Scheduler.Options;

public sealed class MaintenanceJobOptions
{
    public const string SectionName = "MaintenanceJobs";

    public bool Enabled { get; init; } = true;
}
