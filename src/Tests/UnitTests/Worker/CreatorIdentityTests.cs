using Shared.Metadata;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Worker;

public sealed class CreatorIdentityTests
{
    [Test]
    [Arguments("youtube:tab", "youtube")]
    [Arguments("YouTube", "youtube")]
    [Arguments("twitch:vod", "twitch")]
    [Arguments("  youtube  ", "youtube")]
    public void NormalizePlatform_Lowercases_And_Strips_Sub_Extractor(string input, string expected)
        => CreatorIdentity.NormalizePlatform(input).ShouldBe(expected);

    [Test]
    public void NormalizePlatform_Picks_First_Non_Blank_Candidate()
        => CreatorIdentity.NormalizePlatform(null, " ", "youtube:tab").ShouldBe("youtube");

    [Test]
    public void NormalizePlatform_Returns_Null_When_All_Blank()
        => CreatorIdentity.NormalizePlatform(null, "", "  ").ShouldBeNull();

    [Test]
    public void NormalizePlatform_Returns_Null_For_Leading_Colon()
        => CreatorIdentity.NormalizePlatform(":tab").ShouldBeNull();

    [Test]
    public void FirstNonBlank_Returns_First_Trimmed_Value()
        => CreatorIdentity.FirstNonBlank(null, "  ", " value ").ShouldBe("value");
}
