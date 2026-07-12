using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using Worker.Services;

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
    public void Channel_Discovery_Uses_Provider_Query_Limit_For_Source_Type()
    {
        var source = CreateSource(incrementalPageSize: 50) with
        {
            SourceType = CreatorSourceType.Shorts,
            ProviderQueryLimits = YouTubeLimits(videos: 10, streams: 20, shorts: 30)
        };

        ChannelDiscoveryConsumerService.EntryLimit(CreatorSourceScanMode.Full, source)
            .ShouldBe(30);

        var options = ChannelDiscoveryConsumerService.BuildOptions(CreatorSourceScanMode.Full, source);
        options.VideoSelection.PlaylistItems.ShouldBe("1:30");
    }

    [Test]
    public void Channel_Discovery_Request_Query_Limit_Overrides_Source_Limit()
    {
        var source = CreateSource(incrementalPageSize: 50) with
        {
            SourceType = CreatorSourceType.Streams,
            ProviderQueryLimits = YouTubeLimits(videos: 10, streams: 20, shorts: 30)
        };
        var requestLimits = YouTubeLimits(videos: 100, streams: 40, shorts: 300);

        ChannelDiscoveryConsumerService.EntryLimit(CreatorSourceScanMode.Full, source, requestLimits)
            .ShouldBe(40);

        var options = ChannelDiscoveryConsumerService.BuildOptions(CreatorSourceScanMode.Full, source, requestLimits);
        options.VideoSelection.PlaylistItems.ShouldBe("1:40");
    }

    [Test]
    public void Channel_Discovery_Provider_Query_Limit_Still_Clamps_To_Mode_Max()
    {
        var source = CreateSource(incrementalPageSize: 50) with
        {
            ProviderQueryLimits = YouTubeLimits(videos: 1_000, streams: null, shorts: null)
        };

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

    private static CreatorSourceDto CreateSource(int incrementalPageSize)
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

    private static CreatorSourceProviderQueryLimits YouTubeLimits(int? videos, int? streams, int? shorts)
        => new()
        {
            Providers =
            {
                ["youtube"] = new CreatorSourceTypeQueryLimits
                {
                    Videos = videos,
                    Streams = streams,
                    Shorts = shorts
                }
            }
        };
}
