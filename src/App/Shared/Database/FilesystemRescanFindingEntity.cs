using NodaTime;

namespace Shared.Database;

public enum FilesystemRescanFindingType
{
    /// <summary>A file referenced by the database that is missing from storage.</summary>
    MissingFile = 0,

    /// <summary>A file present in storage that the database does not know about (added outside the FrostStream workflow).</summary>
    UnexpectedFile = 1
}

public sealed class FilesystemRescanFindingEntity
{
    public long Id { get; init; }

    public required string StorageKey { get; set; }

    public required string StoragePath { get; set; }

    public required FilesystemRescanFindingType FindingType { get; set; }

    public Guid? MediaGuid { get; set; }

    public Instant DetectedAt { get; set; }

    public Instant LastSeenAt { get; set; }

    public Instant? ResolvedAt { get; set; }
}
