using NodaTime;

namespace Shared.Messaging;

/// <summary>Which rendition pipeline a progress frame belongs to.</summary>
public enum RenditionKind
{
    Stream = 0,
    Audio = 1
}

public static class RenditionProgressSubjects
{
    /// <summary>
    /// Non-persistent broadcast published by MediaProcessor while a rendition encode is running.
    /// Advisory and live-only, exactly like <see cref="DownloadSubjects.DownloadProgress"/>: the
    /// rendition row's status column stays the authoritative record, and a new subscriber receives
    /// frames from the moment it subscribes onward (no JetStream, no replay).
    /// </summary>
    public const string Progress = "rendition.evt.progress";
}

/// <summary>
/// Well-known <see cref="RenditionProgress.Phase"/> values, in pipeline order. Terminal phases are
/// <see cref="Ready"/> and <see cref="Failed"/>, matching the rendition status enums.
/// </summary>
public static class RenditionProgressPhases
{
    public const string FetchingSource = "FetchingSource";
    public const string Probing = "Probing";
    public const string Encoding = "Encoding";
    public const string Packaging = "Packaging";
    public const string Uploading = "Uploading";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}

/// <summary>
/// Advisory event emitted while MediaProcessor produces a stream or audio rendition. Intentionally
/// not persisted anywhere — consumers treat it as a live delta on top of the rendition status they
/// query separately.
/// </summary>
public sealed record RenditionProgress
{
    public required Guid RenditionId { get; init; }
    public required RenditionKind Kind { get; init; }
    public required Guid MediaGuid { get; init; }

    /// <summary>Monotonic per-rendition frame sequence assigned by MediaProcessor.</summary>
    public required int Sequence { get; init; }

    public required Instant OccurredAt { get; init; }

    /// <summary>One of <see cref="RenditionProgressPhases"/>.</summary>
    public required string Phase { get; init; }

    public double? Percent { get; init; }

    /// <summary>Encode speed relative to realtime (ffmpeg's <c>speed=</c>), e.g. 3.2.</summary>
    public double? SpeedX { get; init; }

    public double? EtaSeconds { get; init; }
    public string? Message { get; init; }
}
