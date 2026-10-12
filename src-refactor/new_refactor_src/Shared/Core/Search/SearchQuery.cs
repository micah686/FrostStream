namespace DataBridge.Search;

public enum SearchCollection { Media, Comments, Captions }
public enum Comparison { Equal, Greater, GreaterOrEqual, Less, LessOrEqual }

/// <summary>Structured predicates, independent of any engine's query language.</summary>
public abstract record SearchFilter
{
    public sealed record Text(string Field, string Value, bool Exclude = false) : SearchFilter;
    public sealed record Number(string Field, Comparison Comparison, long Value) : SearchFilter;
    public sealed record All(IReadOnlyList<SearchFilter> Filters) : SearchFilter;
    public sealed record Any(IReadOnlyList<SearchFilter> Filters) : SearchFilter;
}

public sealed record SearchSort(string Field, bool Descending);
public sealed class SearchRequest(string text, string fields)
{
    public string Text { get; } = text;
    public IReadOnlyList<string> Fields { get; } = fields.Split(',');
    public IReadOnlyList<int>? Weights { get; init; }
    public SearchFilter? Filter { get; init; }
    public SearchSort? SortBy { get; set; }
    public int Page { get; init; } = 1;
    public int PerPage { get; init; } = 24;
    public string? IncludeFields { get; init; }
}

public sealed record SearchDocumentHit<T>(T Document);
public sealed record SearchPage<T>(IReadOnlyList<SearchDocumentHit<T>> Hits, int Found);
public interface ISearchReader
{
    Task<SearchPage<T>> SearchAsync<T>(SearchCollection collection, SearchRequest request, CancellationToken ct = default) where T : class;
}
