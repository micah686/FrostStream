namespace Scheduler.Options;

public sealed class NatsOptions
{
    public const string SectionName = "NATS";

    public string? Url { get; init; }

    public string? Token { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? CredsFile { get; init; }
}
