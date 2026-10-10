using System.Text.Json;
using DataBridge.Search;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Phase0;

public sealed class SearchCorpusTests
{
    [Test]
    public void Current_Parser_Matches_Versioned_Characterization_Corpus()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "search-corpus.json")));
        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            var parsed = AdvancedQueryParser.Parse(item.GetProperty("query").GetString());
            parsed.FreeText.ShouldBe(item.GetProperty("text").GetString(), id);
            parsed.FilterParts.ShouldBe(item.GetProperty("filters").EnumerateArray().Select(x => x.GetString()!).ToArray(), id);
            parsed.EffectiveQuery.ShouldBe(string.IsNullOrWhiteSpace(parsed.FreeText) ? "*" : parsed.FreeText, id);
        }
    }
}
