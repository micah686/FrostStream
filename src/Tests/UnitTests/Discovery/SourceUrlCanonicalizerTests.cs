using Shared.Downloads;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Discovery;

public sealed class SourceUrlCanonicalizerTests
{
    [Test]
    [Arguments("https://www.youtube.com/@Name", "https://youtube.com/@Name")]
    [Arguments("https://YouTube.com/@Name/", "https://youtube.com/@Name")]
    [Arguments("https://m.youtube.com/@Name", "https://youtube.com/@Name")]
    [Arguments("  https://youtube.com/@Name  ", "https://youtube.com/@Name")]
    [Arguments("http://youtube.com/@Name", "https://youtube.com/@Name")]
    public void Canonicalize_Normalizes_Host_Scheme_And_Whitespace(string input, string expected)
        => SourceUrlCanonicalizer.Canonicalize(input).ShouldBe(expected);

    [Test]
    [Arguments("https://youtube.com/@Name/videos", "https://youtube.com/@Name")]
    [Arguments("https://www.youtube.com/@Name/videos/", "https://youtube.com/@Name")]
    [Arguments("https://youtube.com/@Name/shorts", "https://youtube.com/@Name")]
    [Arguments("https://youtube.com/@Name/streams", "https://youtube.com/@Name")]
    [Arguments("https://youtube.com/channel/UC123/featured", "https://youtube.com/channel/UC123")]
    [Arguments("https://youtube.com/c/Name/playlists", "https://youtube.com/c/Name")]
    [Arguments("https://youtube.com/user/Name/videos", "https://youtube.com/user/Name")]
    public void Canonicalize_Strips_YouTube_Channel_Tab_Suffixes(string input, string expected)
        => SourceUrlCanonicalizer.Canonicalize(input).ShouldBe(expected);

    [Test]
    public void Canonicalize_Keeps_Tab_Segment_On_Non_Channel_Paths()
        => SourceUrlCanonicalizer.Canonicalize("https://youtube.com/results/videos")
            .ShouldBe("https://youtube.com/results/videos");

    [Test]
    [Arguments("https://youtube.com/watch?v=abc&utm_source=share", "https://youtube.com/watch?v=abc")]
    [Arguments("https://youtube.com/watch?si=xyz&v=abc", "https://youtube.com/watch?v=abc")]
    [Arguments("https://youtube.com/watch?v=abc&feature=shared&fbclid=123", "https://youtube.com/watch?v=abc")]
    public void Canonicalize_Drops_Tracking_Params(string input, string expected)
        => SourceUrlCanonicalizer.Canonicalize(input).ShouldBe(expected);

    [Test]
    public void Canonicalize_Preserves_And_Sorts_Meaningful_Params()
        => SourceUrlCanonicalizer.Canonicalize("https://youtube.com/watch?v=abc&list=PL123")
            .ShouldBe("https://youtube.com/watch?list=PL123&v=abc");

    [Test]
    public void Canonicalize_Does_Not_Upgrade_Unknown_Hosts_To_Https()
        => SourceUrlCanonicalizer.Canonicalize("http://selfhosted.example/channel/foo")
            .ShouldBe("http://selfhosted.example/channel/foo");

    [Test]
    [Arguments("not a url")]
    [Arguments("ftp://example.com/file")]
    public void Canonicalize_Returns_Trimmed_Input_When_Not_Http(string input)
        => SourceUrlCanonicalizer.Canonicalize($"  {input} ").ShouldBe(input);

    [Test]
    public void Canonicalize_Strips_Fragment_And_Root_Path()
        => SourceUrlCanonicalizer.Canonicalize("https://youtube.com/#top")
            .ShouldBe("https://youtube.com");

    [Test]
    public void Canonicalize_Is_Idempotent()
    {
        var once = SourceUrlCanonicalizer.Canonicalize("HTTP://WWW.YouTube.com/@Name/Videos/?si=x&utm_campaign=y");
        SourceUrlCanonicalizer.Canonicalize(once).ShouldBe(once);
        once.ShouldBe("https://youtube.com/@Name");
    }
}
