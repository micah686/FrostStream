using Shouldly;
using TUnit.Core;
using Worker.Metadata;
using YtDlpSharpLib.Models;

namespace UnitTests.Worker;

public sealed class PlaceholderContentDetectorTests
{
    [Test]
    public void Detects_Known_YouTube_Alternative_Client_Placeholder_Id()
    {
        var info = new VideoInfo
        {
            Id = "aQvGIIdgFDM",
            Extractor = "youtube"
        };

        PlaceholderContentDetector.IsPlaceholderMetadata(info).ShouldBeTrue();
    }

    [Test]
    public void Detects_Known_YouTube_Alternative_Client_Display_Id()
    {
        var info = new VideoInfo
        {
            DisplayId = "aQvGIIdgFDM",
            ExtractorKey = "Youtube"
        };

        PlaceholderContentDetector.IsPlaceholderMetadata(info).ShouldBeTrue();
    }

    [Test]
    public void Detects_App_Unavailable_Phrase_In_Title_Fields()
    {
        var title = new VideoInfo
        {
            Title = "This content is not available on this app",
            Extractor = "youtube"
        };
        var fullTitle = new VideoInfo
        {
            FullTitle = "Content is not available on this app",
            Extractor = "youtube"
        };
        var altTitle = new VideoInfo
        {
            AltTitle = "Video is not available on this app",
            Extractor = "youtube"
        };

        PlaceholderContentDetector.IsPlaceholderMetadata(title).ShouldBeTrue();
        PlaceholderContentDetector.IsPlaceholderMetadata(fullTitle).ShouldBeTrue();
        PlaceholderContentDetector.IsPlaceholderMetadata(altTitle).ShouldBeTrue();
    }

    [Test]
    public void Does_Not_Flag_Non_YouTube_Metadata_With_Similar_Wording()
    {
        var info = new VideoInfo
        {
            Id = "aQvGIIdgFDM",
            Title = "This content is not available on this app",
            Extractor = "vimeo"
        };

        PlaceholderContentDetector.IsPlaceholderMetadata(info).ShouldBeFalse();
    }

    [Test]
    public void Detects_Configured_Content_Hashes()
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "0123456789abcdef0123456789abcdef"
        };

        PlaceholderContentDetector
            .IsPlaceholderContentHash("0123456789ABCDEF0123456789ABCDEF", hashes)
            .ShouldBeTrue();
    }
}
