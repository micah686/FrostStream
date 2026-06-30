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

    public string? ConfigSetKey { get; set; }

    public bool EncodeForPlaylist { get; set; }

    public AudioRenditionFormat AudioFormat { get; set; } = AudioRenditionFormat.Aac;

    public string? CookieSecretPath { get; set; }

    public string? YtDlpOptionsJson { get; set; }

    public int Priority { get; set; }

    public bool FetchComments { get; set; }

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

public class UserPlaylistEntity
{
    public Guid PlaylistId { get; set; }

    public required string OwnerSubject { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}

public class UserPlaylistItemEntity
{
    public long Id { get; set; }

    public Guid PlaylistId { get; set; }

    public Guid MediaGuid { get; set; }

    public int Position { get; set; }

    public Instant AddedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();
}
