using NodaTime;
using Shouldly;
using TUnit.Core;
using Worker.Metadata;
using YtDlpSharpLib.Models;

namespace UnitTests.Worker;

public sealed class YtDlpMetadataMapperTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 20, 0);

    [Test]
    public void Map_Projects_Core_Account_Technical_And_Collection_Metadata()
    {
        var info = new VideoInfo
        {
            Id = "video-1",
            DisplayId = "display-1",
            Title = "  Test Video  ",
            FullTitle = "Fallback Title",
            Description = "description",
            Uploader = "Uploader Name",
            UploaderId = "uploader-id",
            UploaderUrl = "https://example.test/uploader",
            ChannelFollowerCount = 123,
            Duration = 12.5,
            ReleaseDate = "20260601",
            WebpageUrl = "https://example.test/watch?v=video-1",
            ViewCount = 1000,
            LikeCount = 50,
            CommentCount = 2,
            Availability = Availability.Unlisted,
            WasLive = true,
            Thumbnails =
            [
                new ThumbnailInfo { Url = "" },
                new ThumbnailInfo { Url = "https://cdn.example.test/thumb.jpg" }
            ],
            Chapters =
            [
                new ChapterInfo { Title = "Intro", StartTime = 0, EndTime = 10 }
            ],
            Subtitles = new Dictionary<string, IReadOnlyList<SubtitleTrack>>
            {
                ["en"] =
                [
                    new SubtitleTrack { Url = "https://cdn.example.test/subs.vtt", Name = "English", Ext = "vtt" }
                ]
            },
            AutomaticCaptions = new Dictionary<string, IReadOnlyList<SubtitleTrack>>
            {
                ["es"] =
                [
                    new SubtitleTrack { Data = "WEBVTT", Ext = "vtt" }
                ]
            },
            Comments =
            [
                new CommentInfo
                {
                    Id = "comment-1",
                    Text = "hello",
                    Author = "Commenter",
                    AuthorId = "commenter-id",
                    Timestamp = 1_801_526_400,
                    LikeCount = long.MaxValue,
                    IsPinned = true
                },
                new CommentInfo { Id = "missing-text" }
            ],
            Tags = [" music ", "Music", ""],
            Categories = ["Education"],
            Artists = ["Artist", "artist", " "],
            AlbumArtists = ["Album Artist"],
            Genre = "Documentary",
            Series = "Series Name",
            Season = "Season 1",
            SeasonNumber = 1,
            Episode = "Episode 2",
            EpisodeNumber = 2,
            Album = "Album",
            Track = "Track",
            TrackNumber = 7,
            ReleaseYear = 2026
        };

        var result = YtDlpMetadataMapper.Map(info, "youtube", new FixedClock(Now));

        result.Account.Platform.ShouldBe("youtube");
        result.Account.AccountName.ShouldBe("Uploader Name");
        result.Account.AccountHandle.ShouldBe("uploader-id");
        result.Account.AccountUrl.ShouldBe("https://example.test/uploader");
        result.Account.FollowerCount.ShouldBe(123);

        result.Media.ExternalMediaId.ShouldBe("video-1");
        result.Media.MetadataScrapeDate.ShouldBe(Now);
        result.Media.Title.ShouldBe("Test Video");
        result.Media.ThumbnailStoragePath.ShouldBe("https://cdn.example.test/thumb.jpg");
        result.Media.DurationSeconds.ShouldBe(12.5);
        result.Media.ReleaseDate.ShouldBe(Instant.FromUtc(2026, 6, 1, 0, 0));
        result.Media.Availability.ShouldBe("unlisted");
        result.Media.WasLive.ShouldBeTrue();

        result.Technical.DurationTicks.ShouldBe(TimeSpan.FromSeconds(12.5).Ticks);
        result.Technical.Chapters.Single().Title.ShouldBe("Intro");
        result.Captions.Count.ShouldBe(2);
        result.Captions.Select(x => x.CaptionType).ShouldBe(["subtitles", "automatic_captions"]);

        var comment = result.Comments.Single();
        comment.CommentId.ShouldBe("comment-1");
        comment.Account.AccountHandle.ShouldBe("commenter-id");
        comment.LikeCount.ShouldBe(int.MaxValue);
        comment.IsPinned.ShouldBeTrue();

        result.Tags.ShouldBe(["music"]);
        result.Categories.ShouldBe(["Education"]);
        result.Artists.ShouldBe(["Artist"]);
        result.AlbumArtists.ShouldBe(["Album Artist"]);
        result.Genres.ShouldBe(["Documentary"]);
        result.Series.ShouldNotBeNull();
        result.Series.SeriesName.ShouldBe("Series Name");
        result.Music.ShouldNotBeNull();
        result.Music.TrackTitle.ShouldBe("Track");
    }

    [Test]
    public void Map_Uses_Unknown_Defaults_And_Fallback_Time_When_Source_Data_Is_Missing()
    {
        var result = YtDlpMetadataMapper.Map(new VideoInfo(), "youtube", new FixedClock(Now));

        result.Account.AccountName.ShouldBe("unknown");
        result.Account.AccountHandle.ShouldBe("unknown");
        result.Media.MetadataScrapeDate.ShouldBe(Now);
        result.Media.ExternalMediaId.ShouldBeNull();
        result.Media.Title.ShouldBeNull();
        result.Media.WasLive.ShouldBeFalse();
        result.Captions.ShouldBeEmpty();
        result.Comments.ShouldBeEmpty();
        result.Series.ShouldBeNull();
        result.Music.ShouldBeNull();
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
