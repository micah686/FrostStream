using System.Globalization;
using Typesense;

namespace DataBridge.Search;

public sealed class TypesenseSearchReader(ITypesenseClient client, TypesenseGenerationRouter router) : ISearchReader
{
    public async Task<SearchPage<T>> SearchAsync<T>(SearchCollection collection, SearchRequest request, CancellationToken ct = default) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var parameters = new SearchParameters(request.Text, string.Join(',', request.Fields))
        {
            FilterBy = Translate(request.Filter),
            QueryByWeights = request.Weights is null ? null : string.Join(',', request.Weights),
            SortBy = request.SortBy is { } sort ? $"{sort.Field}:{(sort.Descending ? "desc" : "asc")},sort_id:asc"
                : collection == SearchCollection.Media ? "_text_match:desc,sort_id:asc" : null,
            Page = Math.Max(1, request.Page),
            PerPage = Math.Clamp(request.PerPage, 1, 250),
            IncludeFields = request.IncludeFields
        };
        var result = await client.Search<T>(router.Name(collection.ToString().ToLowerInvariant()), parameters, ct);
        return new SearchPage<T>(result.Hits.Select(x => new SearchDocumentHit<T>(x.Document)).ToArray(), result.Found);
    }

    public static string? Translate(SearchFilter? filter) => filter switch
    {
        null => null,
        SearchFilter.Text text => text.Field + (text.Exclude ? ":!=" : ":=") + Quote(text.Value),
        SearchFilter.Number number => number.Field + (number.Comparison switch
        {
            Comparison.Equal => ":=", Comparison.Greater => ":>", Comparison.GreaterOrEqual => ":>=",
            Comparison.Less => ":<", Comparison.LessOrEqual => ":<=", _ => throw new ArgumentOutOfRangeException()
        }) + number.Value.ToString(CultureInfo.InvariantCulture),
        SearchFilter.All all => Join(all.Filters, " && "),
        SearchFilter.Any any => Join(any.Filters, " || "),
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

    private static string? Join(IReadOnlyList<SearchFilter> filters, string op)
        => filters.Count == 0 ? null : "(" + string.Join(op, filters.Select(Translate)) + ")";
    private static string Quote(string value) => "`" + value.Replace("`", "\\`", StringComparison.Ordinal) + "`";
}
