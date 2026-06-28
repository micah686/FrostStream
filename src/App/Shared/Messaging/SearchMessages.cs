namespace Shared.Messaging;

/// <summary>
/// A single unified-search result: the matched media card plus why it surfaced. <see cref="MatchedIn"/>
/// holds one or more <see cref="SearchMatch"/> reasons (e.g. a hit on the title and the subtitles).
/// </summary>
public sealed record SearchHitDto
{
    public required MetadataCardDto Media { get; init; }
    public IReadOnlyList<string> MatchedIn { get; init; } = [];
}

public sealed record SearchQueryRequestMessage
{
    public required string Query { get; init; }
    public string Scope { get; init; } = SearchScope.All;
    public int PageSize { get; init; } = 24;
    public int Page { get; init; } = 1;
    public string? SortBy { get; init; }
    public string SortOrder { get; init; } = "desc";
    public string? OwnerSubject { get; init; }
}

public sealed record SearchQueryResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SearchHitDto> Items { get; init; } = [];
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed record SearchSimilarRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public int PageSize { get; init; } = 12;
    public string? OwnerSubject { get; init; }
}

public sealed record SearchSimilarResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SearchHitDto> Items { get; init; } = [];
}
