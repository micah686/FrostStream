using DataBridge.Search;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

public sealed class AdvancedQueryParserTests
{
    [Test]
    public async Task Free_Text_Only_Has_No_Filters()
    {
        var parsed = AdvancedQueryParser.Parse("graphics card");

        parsed.FreeText.ShouldBe("graphics card");
        parsed.EffectiveQuery.ShouldBe("graphics card");
        parsed.FilterParts.ShouldBeEmpty();
        parsed.HasFilters.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Operators_Only_Yields_Wildcard_Query()
    {
        var parsed = AdvancedQueryParser.Parse("codec:h264");

        parsed.HasFreeText.ShouldBeFalse();
        parsed.EffectiveQuery.ShouldBe("*");
        parsed.FilterParts.ShouldContain("(video_codec:=`h264` || audio_codec:=`h264`)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Channel_And_Free_Text_Are_Split()
    {
        var parsed = AdvancedQueryParser.Parse("channel:LinusTechTips graphics card");

        parsed.FreeText.ShouldBe("graphics card");
        parsed.FilterParts.ShouldContain("(account_handle:=`LinusTechTips` || account_name:=`LinusTechTips`)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Quoted_Value_Preserves_Spaces()
    {
        var parsed = AdvancedQueryParser.Parse("channel:\"Linus Tech Tips\"");

        parsed.FilterParts.ShouldContain("(account_handle:=`Linus Tech Tips` || account_name:=`Linus Tech Tips`)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Unknown_Field_Is_Treated_As_Free_Text()
    {
        var parsed = AdvancedQueryParser.Parse("foo:bar");

        parsed.FreeText.ShouldBe("foo:bar");
        parsed.FilterParts.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Technical_Filters_Are_Mapped()
    {
        var parsed = AdvancedQueryParser.Parse("resolution:1080p hdr:true audio:5.1");

        parsed.FilterParts.ShouldContain("resolution_label:=`1080p`");
        parsed.FilterParts.ShouldContain("hdr_type:!=`SDR`");
        parsed.FilterParts.ShouldContain("audio_channels:=6");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Numeric_And_Date_Ranges_Are_Mapped()
    {
        var parsed = AdvancedQueryParser.Parse("after:2023 duration:>600 views:>10000");

        // 2023-01-01T00:00:00Z
        parsed.FilterParts.ShouldContain("release_date_unix:>1672531200");
        parsed.FilterParts.ShouldContain("duration_seconds:>600");
        parsed.FilterParts.ShouldContain("view_count:>10000");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Bare_Numeric_Defaults_To_At_Least()
    {
        var parsed = AdvancedQueryParser.Parse("duration:600");

        parsed.FilterParts.ShouldContain("duration_seconds:>=600");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Codec_Aliases_Are_Normalized()
    {
        var parsed = AdvancedQueryParser.Parse("codec:h265");

        parsed.FilterParts.ShouldContain("(video_codec:=`hevc` || audio_codec:=`hevc`)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Multiple_Filters_Join_With_And()
    {
        var parsed = AdvancedQueryParser.Parse("platform:youtube tag:gaming");

        parsed.FilterBy.ShouldBe("platform:=`youtube` && tags:=`gaming`");
        await Task.CompletedTask;
    }
}
