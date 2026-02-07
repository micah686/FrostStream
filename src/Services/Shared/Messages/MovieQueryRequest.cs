namespace Shared.Messages;

public record MovieQueryRequest
{
    public string? TitleSearch { get; init; }
    public int? ReleaseYear { get; init; }
    public bool IncludeUnverified { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
