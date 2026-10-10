namespace Scheduler.Options;

public sealed class ChannelJobOptions
{
    public const string SectionName = "ChannelJobs";

    public bool Enabled { get; init; } = true;
}
