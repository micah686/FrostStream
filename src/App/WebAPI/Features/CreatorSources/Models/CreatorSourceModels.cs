using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NodaTime;
using Shared.Database;

namespace WebAPI.Features.CreatorSources.Models;

public abstract class CreatorSourceRequestBase
{
    [Required]
    [StringLength(50, MinimumLength = 1)]
    public required string Platform { get; init; }

    public CreatorSourceType SourceType { get; init; }

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

    [DefaultValue("youtube")]
    [StringLength(50, MinimumLength = 1)]
    public string Platform { get; init; } = "youtube";

    public CreatorSourceType SourceType { get; init; } = CreatorSourceType.Videos;

    [DefaultValue("default")]
    public string? StorageKey { get; init; }
}

public sealed record ChannelDownloadResponse(
    long SourceId,
    string SourceUrl,
    string Platform,
    CreatorSourceType SourceType,
    bool Queued,
    string IdempotencyKey);

public sealed class CreatorSourceResponse
{
    public required long Id { get; init; }
    public required string Platform { get; init; }
    public required CreatorSourceType SourceType { get; init; }
    public required string SourceUrl { get; init; }
    public required bool ScanEnabled { get; init; }
    public required int IncrementalPageSize { get; init; }
    public required int ConsecutiveKnownThreshold { get; init; }
    public required int FullRescanIntervalDays { get; init; }
    public required int MetadataRefreshWindow { get; init; }
    public Instant? LastSuccessfulScanAt { get; init; }
    public Instant? LastFullScanAt { get; init; }
    public string? LastSeenHighWatermark { get; init; }
    public int? NextFullScanStartIndex { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
