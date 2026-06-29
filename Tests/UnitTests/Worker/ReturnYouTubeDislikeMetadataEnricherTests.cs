using NodaTime;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Worker.Metadata;
using Worker.Services;
using YtDlpSharpLib.Models;

namespace UnitTests.Worker;

public sealed class ReturnYouTubeDislikeMetadataEnricherTests
{
    [Test]
    public async Task EnrichAsync_Adds_ReturnYouTubeDislike_Counts_For_YouTube_Metadata()
    {
        var client = Substitute.For<IReturnYouTubeDislikeClient>();
        client.GetVotesAsync("video-1", Arg.Any<CancellationToken>())
            .Returns(new ReturnYouTubeDislikeVotes
            {
                Likes = 100,
                Dislikes = 12,
                Rating = 4.25,
                ViewCount = 1000
            });
        var info = new VideoInfo
        {
            Id = "video-1",
            Extractor = "youtube",
            Title = "Example",
            Uploader = "Channel",
            Formats = []
        };

        var enriched = await ReturnYouTubeDislikeMetadataEnricher.EnrichAsync(
            info,
            client,
            CancellationToken.None);

        enriched.LikeCount.ShouldBe(100);
        enriched.DislikeCount.ShouldBe(12);
        enriched.AverageRating.ShouldBe(4.25);
        enriched.ViewCount.ShouldBe(1000);

        var mapped = YtDlpMetadataMapper.Map(enriched, "youtube", SystemClock.Instance);
        mapped.Media.DislikeCount.ShouldBe(12);
    }

    [Test]
    public async Task EnrichAsync_Does_Not_Call_Client_For_Non_YouTube_Metadata()
    {
        var client = Substitute.For<IReturnYouTubeDislikeClient>();
        var info = new VideoInfo
        {
            Id = "video-1",
            Extractor = "vimeo",
            Title = "Example"
        };

        var enriched = await ReturnYouTubeDislikeMetadataEnricher.EnrichAsync(
            info,
            client,
            CancellationToken.None);

        enriched.ShouldBeSameAs(info);
        _ = client.DidNotReceiveWithAnyArgs().GetVotesAsync(default!, default);
    }

    [Test]
    public async Task EnrichAsync_Does_Not_Overwrite_Existing_YtDlp_Counts()
    {
        var client = Substitute.For<IReturnYouTubeDislikeClient>();
        client.GetVotesAsync("video-1", Arg.Any<CancellationToken>())
            .Returns(new ReturnYouTubeDislikeVotes
            {
                Likes = 100,
                Dislikes = 12,
                Rating = 4.25,
                ViewCount = 1000
            });
        var info = new VideoInfo
        {
            Id = "video-1",
            ExtractorKey = "Youtube",
            Title = "Example",
            LikeCount = 5,
            DislikeCount = 2,
            AverageRating = 3.5,
            ViewCount = 50
        };

        var enriched = await ReturnYouTubeDislikeMetadataEnricher.EnrichAsync(
            info,
            client,
            CancellationToken.None);

        enriched.LikeCount.ShouldBe(5);
        enriched.DislikeCount.ShouldBe(2);
        enriched.AverageRating.ShouldBe(3.5);
        enriched.ViewCount.ShouldBe(50);
    }
}
