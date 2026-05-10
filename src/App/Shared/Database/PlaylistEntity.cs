using NodaTime;
using Shared.Messaging;

namespace Shared.Database;

public class PlaylistEntity
{
    public Guid PlaylistId { get; set; }

    public Guid CorrelationId { get; set; }

    public PlaylistState State { get; set; }

    public required string SourceUrl { get; set; }

    public string? RequestedBy { get; set; }

    public string? StorageKey { get; set; }

    public string? ProviderPlaylistId { get; set; }

    public string? Title { get; set; }

    public int TotalItems { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? CompletedAt { get; set; }

    public Instant? LastScannedAt { get; set; }
}

public class PlaylistItemEntity
{
    public long Id { get; set; }

    public Guid PlaylistId { get; set; }

    public Guid JobId { get; set; }

    public int PlaylistIndex { get; set; }

    public required string EntryUrl { get; set; }

    public string? EntryTitle { get; set; }
}

public class PlaylistScanEntryEntity
{
    public long Id { get; set; }

    public Guid PlaylistId { get; set; }

    public int PlaylistIndex { get; set; }

    public required string EntryUrl { get; set; }

    public string? EntryTitle { get; set; }
}

public class MediaPlaylistMembershipEntity
{
    public long Id { get; set; }

    public Guid MediaGuid { get; set; }

    public Guid PlaylistId { get; set; }

    public int PlaylistIndex { get; set; }
}

public class PlaylistMetadataEntity
{
    public long Id { get; set; }

    public Guid PlaylistId { get; set; }

    public string? Title { get; set; }
}
