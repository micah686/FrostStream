using NodaTime;

namespace Shared.Messaging;

public static class WorkerRegistrySubjects
{
    public const string Heartbeat = "worker-registry.heartbeat";
    public const string List = "worker-registry.list";
    public const string UpdateCapacity = "worker-registry.update-capacity";
    public const string AcquireCollection = "worker-registry.acquire-collection";
    public const string RenewCollection = "worker-registry.renew-collection";
    public const string ReleaseCollection = "worker-registry.release-collection";
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

public sealed record WorkerCapacityUpdateRequest
{
    public required string WorkerId { get; init; }
    public int MaxActiveJobs { get; init; }
}

public sealed record WorkerCapacityUpdateResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed record CollectionCapacityRequest
{
    public Guid DispatchId { get; init; }
    public Guid? GroupId { get; init; }
    public required string WorkerId { get; init; }
}

public sealed record CollectionCapacityResponse
{
    public bool Granted { get; init; }
    public bool Stale { get; init; }
}

public sealed record WorkerInfo
{
    public required string WorkerId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string IncomingRoot { get; init; }
    public required Instant LastOnline { get; init; }
    public int MaxActiveJobs { get; init; } = 2;
    public int ActiveJobs { get; init; }
}
