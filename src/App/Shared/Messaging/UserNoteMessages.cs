using NodaTime;

namespace Shared.Messaging;

public static class UserNoteSubjects
{
    public const string Upsert = "notes.user.upsert";
    public const string Get = "notes.user.get";
    public const string Delete = "notes.user.delete";
    public const string Search = "notes.user.search";
}

public static class UserNoteTargetTypes
{
    public const string Video = "video";
    public const string Channel = "channel";
    public const string Playlist = "playlist";

    public static string? Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "video" or "videos" => Video,
            "channel" or "channels" => Channel,
            "playlist" or "playlists" => Playlist,
            _ => null
        };
}

public sealed record UserNoteDto
{
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
    public required string Note { get; init; }
    public required Instant CreatedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
}

public sealed record UserNoteUpsertRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
    public required string Note { get; init; }
}

public sealed record UserNoteGetRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
}

public sealed record UserNoteDeleteRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
}

public sealed record UserNoteSearchRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string Query { get; init; }
    public string? TargetType { get; init; }
    public int PageSize { get; init; } = 50;
    public int PageOffset { get; init; }
}

public sealed record UserNoteResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public UserNoteDto? Note { get; init; }
}

public sealed record UserNoteSearchResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<UserNoteDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}
