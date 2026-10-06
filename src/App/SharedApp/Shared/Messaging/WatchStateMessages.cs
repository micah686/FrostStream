using NodaTime;

namespace Shared.Messaging;

public static class WatchStateSubjects
{
    public const string Upsert = "media.watch-state.upsert";
    public const string Get = "media.watch-state.get";
    public const string ListInProgress = "media.watch-state.list-in-progress";
    public const string ListHistory = "media.watch-state.history.list";
}

public sealed record WatchStateUpsertRequest
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool Completed { get; init; }
}

public sealed record WatchStateGetRequest
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
}

public sealed record WatchStateResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public WatchStateDto? State { get; init; }
}

public sealed record WatchStateInProgressListRequest
{
    public required string OwnerSubject { get; init; }
    public int Limit { get; init; } = 12;
}

public sealed record WatchStateHistoryListRequest
{
    public required string OwnerSubject { get; init; }
    public int PageSize { get; init; } = 24;
    public int Page { get; init; } = 1;
}

public sealed record WatchStateListResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<WatchStateDto>? Items { get; init; }
}

public sealed record WatchStateHistoryListResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<WatchHistoryItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record WatchHistoryItemDto
{
    public required WatchStateDto WatchState { get; init; }
    public required MetadataCardDto Media { get; init; }
}

public sealed record WatchStateDto
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool Completed { get; init; }
    public Instant? WatchedAt { get; init; }
    public required Instant LastPlayedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
}

public static class MediaLikeSubjects
{
    public const string Get = "media.likes.get";
    public const string Like = "media.likes.like";
    public const string Unlike = "media.likes.unlike";
    public const string List = "media.likes.list";
}

public sealed record MediaLikeStateRequest
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
}

public sealed record MediaLikeListRequest
{
    public required string OwnerSubject { get; init; }
    public int PageSize { get; init; } = 24;
    public int Page { get; init; } = 1;
}

public sealed record MediaLikeStateResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public MediaLikeStateDto? State { get; init; }
}

public sealed record MediaLikeListResponse
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<LikedMediaItemDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record LikedMediaItemDto
{
    public required MediaLikeStateDto Like { get; init; }
    public required MetadataCardDto Media { get; init; }
}

public sealed record MediaLikeStateDto
{
    public required string OwnerSubject { get; init; }
    public required Guid MediaGuid { get; init; }
    public required bool Liked { get; init; }
    public Instant? LikedAt { get; init; }
    public Instant? UpdatedAt { get; init; }
}
