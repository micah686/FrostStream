namespace Shared.Database;
public enum CreatorSourceType
{
    Videos = 0,
    Shorts = 1,
    Streams = 2,
    Playlist = 3,
    Clips = 4,
    Vods = 5
}

public enum CreatorSourceScanMode
{
    Incremental = 0,
    Full = 1
}

public enum MediaDiscoveryStatus
{
    Discovered = 0,
    Queued = 1,
    Ignored = 2,
    PossiblyUnavailable = 3,
    Unavailable = 4,
    RemovedFromSource = 5
}

public enum MediaMetadataStatus
{
    PendingEnrichment = 0,
    RefreshRequested = 1,
    Enriched = 2,
    Failed = 3
}
