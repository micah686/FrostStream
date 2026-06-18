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
    public void Playlist_Metadata_Fetch_Uses_Hard_Entry_Limit()
    {
        var options = PlaylistCommandsConsumerService.BuildPlaylistOptions();

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
            MetadataRefreshWindow = 25,
            CreatedAt = NodaTime.Instant.FromUtc(2026, 6, 18, 0, 0)
        };
}
