using Shared.Database;

namespace Shared.Messaging;

/// <summary>Worker → DataBridge request for the expected file inventory.</summary>
public sealed record FilesystemRescanInventoryRequest;

/// <summary>A single content file the database expects to exist in storage.</summary>
public sealed record FilesystemContentPathDto
{
    public required string StoragePath { get; init; }
    public required Guid MediaGuid { get; init; }
}

/// <summary>The expected content files for one storage key.</summary>
public sealed record FilesystemStorageInventoryDto
{
    public required string StorageKey { get; init; }
    public required IReadOnlyList<FilesystemContentPathDto> Paths { get; init; }
}

public sealed record FilesystemRescanInventoryResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Expected content files grouped by storage key.</summary>
    public IReadOnlyList<FilesystemStorageInventoryDto> Storages { get; init; } = [];

    /// <summary>
    /// Additional non-content storage paths the database tracks (thumbnails, captions,
    /// info.json sidecars, channel avatars/banners). These do not carry a storage key,
    /// so they are treated as expected across every storage key to avoid false
    /// "unexpected file" findings.
    /// </summary>
    public IReadOnlyList<string> SidecarPaths { get; init; } = [];
}

/// <summary>A single reconciliation finding produced by the Worker.</summary>
public sealed record FilesystemRescanFindingDto
{
    public required string StoragePath { get; init; }
    public required FilesystemRescanFindingType FindingType { get; init; }
    public Guid? MediaGuid { get; init; }
}

/// <summary>
/// Worker → DataBridge report of the current findings for a single storage key.
/// The set is authoritative for that storage key: any previously-open finding not
/// present here is marked resolved.
/// </summary>
public sealed record FilesystemRescanReportRequest
{
    public required string ScheduleKey { get; init; }
    public required string StorageKey { get; init; }
    public required IReadOnlyList<FilesystemRescanFindingDto> Findings { get; init; }
}

public sealed record FilesystemRescanReportResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int OpenCount { get; init; }
    public int ResolvedCount { get; init; }
}
