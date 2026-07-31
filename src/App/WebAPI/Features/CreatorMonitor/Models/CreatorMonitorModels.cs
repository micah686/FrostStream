using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.CreatorMonitor.Models;

public abstract class CreatorSourceRequestBase
{
    [Required]
    [Url]
    [StringLength(4096, MinimumLength = 1)]
    public required string SourceUrl { get; init; }

    public bool ScanEnabled { get; init; } = true;

    [Range(1, 500)]
    public int IncrementalPageSize { get; init; } = 50;

    [Range(1, 500)]
    public int ConsecutiveKnownThreshold { get; init; } = 25;

    [Range(1, 365)]
    public int FullRescanIntervalDays { get; init; } = 30;

    /// <summary>Minimum hours between incremental update-check scans; the global sweep tick only
    /// scans sources that are due.</summary>
    [Range(1, 168)]
    public int UpdateCheckIntervalHours { get; init; } = 6;

    [Range(1, 500)]
    public int MetadataRefreshWindow { get; init; } = 25;

}

public sealed class CreatorSourceCreateRequest : CreatorSourceRequestBase;

public sealed class CreatorSourceUpdateRequest : CreatorSourceRequestBase;

public sealed class ChannelDownloadRequest
{
    [Required]
    [Url]
    [StringLength(4096, MinimumLength = 1)]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public string? StorageKey { get; init; }

    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? ConfigSetKey { get; init; }

    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? CookieProfileKey { get; init; }

    public YtDlpOptions? YtDlpOptions { get; init; }

    public bool? EncodeForPlaylist { get; init; }

    [Range(0, 100)]
    public int? Priority { get; init; }

    public bool? FetchComments { get; init; }

    /// <summary>Re-download videos even when the same source is already present in the library.</summary>
    public bool ForceDownload { get; init; }

}

public sealed record ChannelDownloadResponse(
    long SourceId,
    Guid CorrelationId,
    string SourceUrl,
    bool Queued,
    string IdempotencyKey);

public sealed class IgnoredMediaResponse
{
    public required long Id { get; init; }
    public required long CreatorSourceId { get; init; }
    public string? Title { get; init; }
    public required string CanonicalUrl { get; init; }
    public string? IgnoredKeyword { get; init; }
    public required Instant FirstSeenAt { get; init; }
    public required Instant LastSeenAt { get; init; }
}

/// <summary>Optional config-set / overrides used when force-queueing a previously ignored video.</summary>
public sealed class ForceQueueMediaRequest
{
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? ConfigSetKey { get; init; }

    [DefaultValue("default")]
    public string? StorageKey { get; init; }

    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? CookieProfileKey { get; init; }

    public YtDlpOptions? YtDlpOptions { get; init; }

    public bool? EncodeForPlaylist { get; init; }

    [Range(0, 100)]
    public int? Priority { get; init; }

    public bool? FetchComments { get; init; }
}

public sealed record ForceQueueResponse(long MediaId, Guid JobId, bool Queued);

public sealed class CreatorSourceResponse
{
    public required long Id { get; init; }
    public required string SourceUrl { get; init; }
    public long? AccountId { get; init; }
    public required bool ScanEnabled { get; init; }
    public required int IncrementalPageSize { get; init; }
    public required int ConsecutiveKnownThreshold { get; init; }
    public required int FullRescanIntervalDays { get; init; }
    public required int UpdateCheckIntervalHours { get; init; }
    public required int MetadataRefreshWindow { get; init; }
    public Instant? LastSuccessfulScanAt { get; init; }
    public Instant? LastFullScanAt { get; init; }
    public string? LastSeenHighWatermark { get; init; }
    public int? NextFullScanStartIndex { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
