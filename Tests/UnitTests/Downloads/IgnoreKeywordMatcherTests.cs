using Shared.Downloads;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Downloads;

public sealed class IgnoreKeywordMatcherTests
{
    [Test]
    public async Task Substring_Matches_Case_Insensitively()
    {
        var keywords = new[] { new IgnoreKeyword { Pattern = "trailer" } };

        var match = IgnoreKeywordMatcher.FirstMatch("Official TRAILER (2026)", keywords);

        match.ShouldNotBeNull();
        match!.Pattern.ShouldBe("trailer");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Substring_Does_Not_Match_Unrelated_Title()
    {
        var keywords = new[] { new IgnoreKeyword { Pattern = "trailer" } };

        IgnoreKeywordMatcher.FirstMatch("Full episode", keywords).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Regex_Matches_Case_Insensitively()
    {
        var keywords = new[]
        {
            new IgnoreKeyword { Pattern = @"^\[live\]", MatchType = IgnoreKeywordMatchType.Regex }
        };

        IgnoreKeywordMatcher.FirstMatch("[LIVE] Stream now", keywords).ShouldNotBeNull();
        IgnoreKeywordMatcher.FirstMatch("Recap of [live] event", keywords).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Invalid_Regex_Is_Skipped_Rather_Than_Throwing()
    {
        var keywords = new[]
        {
            new IgnoreKeyword { Pattern = "(unclosed", MatchType = IgnoreKeywordMatchType.Regex }
        };

        IgnoreKeywordMatcher.FirstMatch("anything", keywords).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Blank_Title_Or_Empty_Keywords_Return_Null()
    {
        var keywords = new[] { new IgnoreKeyword { Pattern = "trailer" } };

        IgnoreKeywordMatcher.FirstMatch(null, keywords).ShouldBeNull();
        IgnoreKeywordMatcher.FirstMatch("   ", keywords).ShouldBeNull();
        IgnoreKeywordMatcher.FirstMatch("Official Trailer", []).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_Accepts_Valid_Patterns()
    {
        IgnoreKeywordMatcher.Validate(new IgnoreKeyword { Pattern = "trailer" }).ShouldBeNull();
        IgnoreKeywordMatcher.Validate(new IgnoreKeyword { Pattern = @"^\[live\]", MatchType = IgnoreKeywordMatchType.Regex }).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_Rejects_Empty_And_Invalid_Regex()
    {
        IgnoreKeywordMatcher.Validate(new IgnoreKeyword { Pattern = "  " }).ShouldNotBeNull();
        IgnoreKeywordMatcher.Validate(new IgnoreKeyword { Pattern = new string('x', IgnoreKeywordMatcher.MaxPatternLength + 1) }).ShouldNotBeNull();
        IgnoreKeywordMatcher.Validate(new IgnoreKeyword { Pattern = "(unclosed", MatchType = IgnoreKeywordMatchType.Regex }).ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Serialize_Roundtrips_Through_Deserialize()
    {
        var keywords = new[]
        {
            new IgnoreKeyword { Pattern = "trailer" },
            new IgnoreKeyword { Pattern = "^live", MatchType = IgnoreKeywordMatchType.Regex }
        };

        var json = IgnoreKeywordMatcher.Serialize(keywords);
        var restored = IgnoreKeywordMatcher.Deserialize(json);

        restored.Count.ShouldBe(2);
        restored[1].MatchType.ShouldBe(IgnoreKeywordMatchType.Regex);
        IgnoreKeywordMatcher.Serialize([]).ShouldBeNull();
        await Task.CompletedTask;
    }
}
