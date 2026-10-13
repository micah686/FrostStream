using System.Text.Json;
using DataBridge.Search;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace FrostStream.Lite.Infrastructure.Search;

internal static class LuceneDocumentCodec
{
    private static readonly HashSet<string> LongFields = ["account_id", "release_date_unix", "release_date_sort", "added_at_sort", "view_count", "like_count", "dislike_count", "video_width", "video_height", "audio_channels", "age_limit", "comment_timestamp_unix"];
    private static readonly HashSet<string> ExactFields = ["id", "sort_id", "media_guid", "parent_comment_id", "account_name", "account_handle", "platform", "tags", "categories", "genres", "artists", "caption_languages", "language_code", "caption_type", "video_codec", "audio_codec", "resolution_label", "hdr_type"];
    public static string Normalize(string value) => value.Normalize().ToLowerInvariant();
    public static string Collection(SearchCollection collection) => collection.ToString().ToLowerInvariant();
    public static Document Encode<T>(SearchCollection collection, T source)
    {
        var json = JsonSerializer.Serialize(source);
        using var parsed = JsonDocument.Parse(json);
        var id = parsed.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Search documents require an ID.");
        if (collection == SearchCollection.Media && parsed.RootElement.GetProperty("title").ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Media requires a title.");
        var document = new Document
        {
            new StringField("_collection", Collection(collection), Field.Store.NO),
            new StringField("_key", Collection(collection) + ":" + id, Field.Store.NO),
            new SortedDocValuesField("_id_sort", new BytesRef(id)),
            new StoredField("_json", json)
        };
        foreach (var property in parsed.RootElement.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (LongFields.Contains(property.Name))
            {
                var number = value.GetInt64();
                document.Add(new Int64Field("n_" + property.Name, number, Field.Store.NO));
                document.Add(new NumericDocValuesField("s_" + property.Name, number));
            }
            else if (property.Name == "duration_seconds")
            {
                var number = value.GetDouble();
                if (!double.IsFinite(number)) throw new InvalidOperationException("Duration must be finite.");
                document.Add(new DoubleField("n_" + property.Name, number, Field.Store.NO));
                document.Add(new DoubleDocValuesField("s_" + property.Name, number));
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) AddText(document, property.Name, item.GetString() ?? "", false);
            }
            else if (value.ValueKind == JsonValueKind.String) AddText(document, property.Name, value.GetString()!, true);
            else if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                document.Add(new StringField("e_" + property.Name, value.GetBoolean() ? "true" : "false", Field.Store.NO));
        }
        return document;
    }
    private static void AddText(Document document, string field, string value, bool sortable)
    {
        if (ExactFields.Contains(field) && System.Text.Encoding.UTF8.GetByteCount(value) <= 32766)
            document.Add(new StringField("e_" + field, Normalize(value), Field.Store.NO));
        document.Add(new TextField("t_" + field, value, Field.Store.NO));
        if (sortable && field == "title") document.Add(new SortedDocValuesField("s_title", new BytesRef(Normalize(value))));
    }
    public static Query Filter(SearchFilter filter) => filter switch
    {
        SearchFilter.Text text when text.Exclude => new BooleanQuery { { new MatchAllDocsQuery(), Occur.MUST }, { new TermQuery(new Term("e_" + text.Field, Normalize(text.Value))), Occur.MUST_NOT } },
        SearchFilter.Text text => new TermQuery(new Term("e_" + text.Field, Normalize(text.Value))),
        SearchFilter.Number number => NumberFilter(number),
        SearchFilter.All all => Group(all.Filters, Occur.MUST),
        SearchFilter.Any any => Group(any.Filters, Occur.SHOULD),
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };
    private static Query Group(IReadOnlyList<SearchFilter> filters, Occur occur)
    {
        if (filters.Count == 0) return occur == Occur.MUST ? new MatchAllDocsQuery() : new BooleanQuery();
        var query = new BooleanQuery();
        foreach (var filter in filters) query.Add(Filter(filter), occur);
        return query;
    }
    private static Query NumberFilter(SearchFilter.Number filter)
    {
        var lower = filter.Comparison is Comparison.Equal or Comparison.Greater or Comparison.GreaterOrEqual;
        var upper = filter.Comparison is Comparison.Equal or Comparison.Less or Comparison.LessOrEqual;
        var includeLower = filter.Comparison != Comparison.Greater;
        var includeUpper = filter.Comparison != Comparison.Less;
        return filter.Field == "duration_seconds"
            ? NumericRangeQuery.NewDoubleRange("n_" + filter.Field, lower ? filter.Value : null, upper ? filter.Value : null, includeLower, includeUpper)
            : NumericRangeQuery.NewInt64Range("n_" + filter.Field, lower ? filter.Value : null, upper ? filter.Value : null, includeLower, includeUpper);
    }
    public static Query Query(SearchCollection collection, SearchRequest request, Analyzer analyzer)
    {
        var query = new BooleanQuery { { new TermQuery(new Term("_collection", Collection(collection))), Occur.MUST } };
        if (request.Filter is not null) query.Add(Filter(request.Filter), Occur.MUST);
        if (request.Text != "*" && !string.IsNullOrWhiteSpace(request.Text))
        {
            // Build term queries directly: user text is never interpreted as Lucene syntax.
            using var stream = analyzer.GetTokenStream("query", new StringReader(request.Text));
            var term = stream.AddAttribute<ICharTermAttribute>();
            stream.Reset();
            var tokens = new List<string>();
            while (stream.IncrementToken())
            {
                if (tokens.Count == 128) throw new ArgumentException("Search text contains too many terms.");
                tokens.Add(term.ToString());
            }
            stream.End();
            if (tokens.Count == 0) query.Add(new BooleanQuery(), Occur.MUST);
            for (var i = 0; i < tokens.Count; i++)
            {
                var alternatives = new BooleanQuery();
                for (var field = 0; field < request.Fields.Count; field++)
                {
                    Query match = i == tokens.Count - 1
                        ? new PrefixQuery(new Term("t_" + request.Fields[field], tokens[i]))
                        : new TermQuery(new Term("t_" + request.Fields[field], tokens[i]));
                    match.Boost = request.Weights is { } weights && field < weights.Count ? weights[field] : 1;
                    alternatives.Add(match, Occur.SHOULD);
                }
                query.Add(alternatives, Occur.MUST);
            }
        }
        return query;
    }
    public static Sort Sort(SearchRequest request)
    {
        var primary = request.SortBy is not { } sort ? SortField.FIELD_SCORE
            : sort.Field == "title" ? new SortField("s_title", SortFieldType.STRING, sort.Descending)
            : sort.Field == "duration_seconds" ? new SortField("s_duration_seconds", SortFieldType.DOUBLE, sort.Descending)
            : LongFields.Contains(sort.Field) ? new SortField("s_" + sort.Field, SortFieldType.INT64, sort.Descending)
            : throw new ArgumentException("Unsupported search sort field.");
        return new Sort(primary, new SortField("_id_sort", SortFieldType.STRING));
    }
}
