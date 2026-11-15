using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using Worker.Services;
using YtDlpSharpLib.Models;

namespace UnitTests.Worker;

public sealed class ScanGuardTests
{
    [Test]
    public void Channel_Discovery_Full_Scan_Uses_Hard_Entry_Limit()
    {
        var source = CreateSource(incrementalPageSize: 50);

        ChannelDiscoveryConsumerService.EntryLimit(CreatorSourceScanMode.Full, source)
            .ShouldBe(ChannelDiscoveryConsumerService.MaxFullScanEntriesPerSource);

        var options = ChannelDiscoveryConsumerService.BuildOptions(CreatorSourceScanMode.Full, source);
        options.VideoSelection.PlaylistItems.ShouldBe($"1:{ChannelDiscoveryConsumerService.MaxFullScanEntriesPerSource}");
    }

    [Test]
    public void Channel_Discovery_Full_Scan_Resumes_From_Cursor()
    {
        var source = CreateSource(incrementalPageSize: 50) with
        {
            NextFullScanStartIndex = 5_001
        };

        ChannelDiscoveryConsumerService.PageStartIndex(CreatorSourceScanMode.Full, source)
            .ShouldBe(5_001);

        var options = ChannelDiscoveryConsumerService.BuildOptions(CreatorSourceScanMode.Full, source);
        options.VideoSelection.PlaylistItems.ShouldBe("5001:10000");
    }

    [Test]
    public void Channel_Discovery_Incremental_Scan_Clamps_Page_Size()
    {
        var source = CreateSource(incrementalPageSize: ChannelDiscoveryConsumerService.MaxIncrementalScanEntries + 1);

        ChannelDiscoveryConsumerService.EntryLimit(CreatorSourceScanMode.Incremental, source)
            .ShouldBe(ChannelDiscoveryConsumerService.MaxIncrementalScanEntries);

        var options = ChannelDiscoveryConsumerService.BuildOptions(CreatorSourceScanMode.Incremental, source);
        options.VideoSelection.PlaylistItems.ShouldBe($"1:{ChannelDiscoveryConsumerService.MaxIncrementalScanEntries}");
    }

    [Test]
    public void Playlist_Metadata_Fetch_Uses_Hard_Entry_Limit()
    {
        var options = PlaylistCommandsConsumerService.BuildPlaylistOptions();

        options.VideoSelection.PlaylistItems.ShouldBe($"1:{PlaylistCommandsConsumerService.MaxPlaylistEntriesPerRequest}");
    }

    [Test]
    public void Playlist_Metadata_Fetch_Uses_Requested_Page_Range()
    {
        var options = PlaylistCommandsConsumerService.BuildPlaylistOptions(pageStartIndex: 5_001, pageSize: 5_000);

        options.VideoSelection.PlaylistItems.ShouldBe("5001:10000");
    }

    [Test]
    public void Playlist_Metadata_Fetch_Clamps_Page_Size_To_Hard_Limit()
    {
        var options = PlaylistCommandsConsumerService.BuildPlaylistOptions(pageStartIndex: 1, pageSize: 10_000);

        options.VideoSelection.PlaylistItems.ShouldBe($"1:{PlaylistCommandsConsumerService.MaxPlaylistEntriesPerRequest}");
    }

    [Test]
    public void Channel_Discovery_Uses_The_Extractor_Url_Instead_Of_A_Collection_Url()
    {
        var source = CreateSource(50) with
        {
            Platform = "example",
            SourceUrl = "https://example.test/creator"
        };
        var entry = new VideoInfo
        {
            Id = "video-id",
            Url = "https://example.test/media/video-id",
            WebpageUrl = source.SourceUrl,
            Extractor = "example"
        };

        ChannelDiscoveryConsumerService.ResolveCanonicalUrl(source, entry, entry.Id)
            .ShouldBe("https://example.test/media/video-id");
    }

    [Test]
    public void Channel_Discovery_Expands_Nested_Collections_Into_Individual_Media()
    {
        var source = CreateSource(50) with { Platform = "example" };
        var container = new VideoInfo
        {
            Type = MetadataType.Playlist,
            Entries =
            [
                new VideoInfo
                {
                    Type = MetadataType.Playlist,
                    Id = "creator-id",
                    Title = "Creator - Videos",
                    WebpageUrl = source.SourceUrl + "/videos",
                    Extractor = "example:collection",
                    Entries =
                    [
                        new VideoInfo
                        {
                            Type = MetadataType.Url,
                            Id = "video-1",
                            Title = "First video",
                            Url = "https://example.test/watch/1"
                        },
                        new VideoInfo
                        {
                            Type = MetadataType.MultiVideo,
                            Id = "album-id",
                            Entries =
                            [
                                new VideoInfo
                                {
                                    Id = "video-2",
                                    Title = "Second video",
                                    WebpageUrl = "https://other.test/media/2",
                                    Extractor = "other"
                                }
                            ]
                        }
                    ]
                },
                new VideoInfo
                {
                    Id = "video-3",
                    Url = "https://example.test/watch/3"
                },
                new VideoInfo
                {
                    Type = MetadataType.Playlist,
                    Id = "empty-collection",
                    WebpageUrl = source.SourceUrl + "/empty"
                },
                new VideoInfo
                {
                    Id = "untyped-empty-collection",
                    WebpageUrl = source.SourceUrl + "/untyped",
                    Entries = []
                }
            ]
        };

        var candidates = ChannelDiscoveryConsumerService.ExtractCandidates(source, container).ToArray();

        candidates.Select(x => x.ExternalMediaId).ShouldBe(["video-1", "video-2", "video-3"]);
        candidates.Select(x => x.CanonicalUrl).ShouldBe(
            ["https://example.test/watch/1", "https://other.test/media/2", "https://example.test/watch/3"]);
        candidates[0].Title.ShouldBe("First video");
        candidates[0].Extractor.ShouldBe("example:collection");
        candidates[1].Platform.ShouldBe("other");
    }

    [Test]
    public void Channel_Discovery_Does_Not_Construct_A_Media_Url_From_A_Collection_Id()
    {
        var source = CreateSource(50);
        var entry = new VideoInfo { Id = "collection-id", WebpageUrl = source.SourceUrl };

        ChannelDiscoveryConsumerService.ResolveCanonicalUrl(source, entry, entry.Id).ShouldBeNull();
    }

    [Test]
    public void Playlist_Metadata_Uses_The_Entry_Id_For_YouTube_Instead_Of_A_Collection_Url()
    {
        var entry = new VideoInfo
        {
            Id = "playlist-video-id",
            WebpageUrl = "https://www.youtube.com/playlist?list=PL123"
        };

        PlaylistCommandsConsumerService.ResolveEntryUrl(
                "https://www.youtube.com/playlist?list=PL123",
                entry)
            .ShouldBe("https://www.youtube.com/watch?v=playlist-video-id");
    }

    [Test]
    public void Channel_Download_Uses_Explicit_Collection_Correlation_Id()
    {
        var correlationId = Guid.NewGuid();
        var request = new ChannelScanFullRequested
        {
            ScheduleKey = "manual-channel-download",
            TaskType = "channel_scan_full",
            DueWindowUtc = NodaTime.Instant.FromUnixTimeSeconds(1),
            IdempotencyKey = "manual-channel-download:42:test",
            OccurredAt = NodaTime.Instant.FromUnixTimeSeconds(1),
            TargetSourceId = 42,
            CorrelationId = correlationId
        };

        ChannelDiscoveryConsumerService.ChannelCorrelationId(request, 42).ShouldBe(correlationId);
    }

    private static CreatorMonitorDto CreateSource(int incrementalPageSize)
        => new()
        {
            Id = 1,
            Platform = "youtube",
            SourceType = CreatorSourceType.Videos,
            SourceUrl = "https://example.test/@creator",
            ScanEnabled = true,
            IncrementalPageSize = incrementalPageSize,
            ConsecutiveKnownThreshold = 25,
            FullRescanIntervalDays = 30,
            UpdateCheckIntervalHours = 6,
            MetadataRefreshWindow = 25,
            CreatedAt = NodaTime.Instant.FromUtc(2026, 6, 18, 0, 0)
        };

}
