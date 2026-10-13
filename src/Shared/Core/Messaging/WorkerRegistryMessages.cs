using NodaTime;

namespace Shared.Messaging;

public static class WorkerRegistrySubjects
{
    public const string Heartbeat = "worker-registry.heartbeat";
    public const string List = "worker-registry.list";
    public const string QueueGroup = "databridge-worker-registry";
}

public sealed record WorkerHeartbeat
{
    public required string WorkerId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string IncomingRoot { get; init; }
    public required Instant ReportedAt { get; init; }
}

public sealed record WorkerRegistryListRequest { public string? Tag { get; init; } }

public sealed record WorkerRegistryListResponse { public IReadOnlyList<WorkerInfo> Workers { get; init; } = []; }

public sealed record WorkerInfo
{
    public required string WorkerId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string IncomingRoot { get; init; }
    public required Instant LastOnline { get; init; }
}
