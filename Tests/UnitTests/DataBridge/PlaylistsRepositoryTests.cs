using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class PlaylistsRepositoryTests
{
    [Test]
    public async Task CreateOrReuse_Inserts_New_Playlist_And_Reuses_By_SourceUrl()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new PlaylistsRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var first = PlaylistRequested("https://example.test/playlist", requestedBy: "micah", storageKey: "default");

        var created = await repo.CreateOrReuseAsync(first);
        var reused = await repo.CreateOrReuseAsync(PlaylistRequested(
            "https://example.test/playlist",
            requestedBy: "other-user",
            storageKey: "archive"));

        created.WasReused.ShouldBeFalse();
        reused.WasReused.ShouldBeTrue();
        reused.Playlist.PlaylistId.ShouldBe(first.PlaylistId);
        reused.Playlist.RequestedBy.ShouldBe("other-user");
        reused.Playlist.StorageKey.ShouldBe("archive");
        reused.Playlist.State.ShouldBe(PlaylistState.PendingMetadata);
        (await db.Playlists.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task ApplyMetadataFetched_Updates_Playlist_And_Upserts_Metadata_Row()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new PlaylistsRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var playlistId = Guid.NewGuid();
        db.Playlists.Add(new PlaylistEntity
        {
            PlaylistId = playlistId,
            CorrelationId = Guid.NewGuid(),
            State = PlaylistState.PendingMetadata,
            SourceUrl = "https://example.test/playlist",
            StorageKey = "default"
        });
        await db.SaveChangesAsync();

        await repo.ApplyMetadataFetchedAsync(playlistId, new PlaylistMetadataFetched
        {
            PlaylistId = playlistId,
            CorrelationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            OperationKey = "playlist/metadata",
            OccurredAt = DataBridgeTestHelpers.Now,
            Attempt = 1,
            ProviderPlaylistId = "provider-playlist",
            Title = "Playlist Title",
            TotalItems = 12,
            Entries = []
        });

        var playlist = await db.Playlists.SingleAsync();
        playlist.State.ShouldBe(PlaylistState.MetadataResolved);
        playlist.ProviderPlaylistId.ShouldBe("provider-playlist");
        playlist.Title.ShouldBe("Playlist Title");
        playlist.TotalItems.ShouldBe(12);
        playlist.LastScannedAt.ShouldBe(DataBridgeTestHelpers.Now);

        var metadata = await db.PlaylistMetadata.SingleAsync();
        metadata.PlaylistId.ShouldBe(playlistId);
        metadata.Title.ShouldBe("Playlist Title");
    }

    [Test]
    public async Task ApplyMetadataFetched_For_Partial_Page_Keeps_Playlist_Pending()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new PlaylistsRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var playlistId = Guid.NewGuid();
        db.Playlists.Add(new PlaylistEntity
        {
            PlaylistId = playlistId,
            CorrelationId = Guid.NewGuid(),
            State = PlaylistState.PendingMetadata,
            SourceUrl = "https://example.test/playlist",
            StorageKey = "default"
        });
        await db.SaveChangesAsync();

        await repo.ApplyMetadataFetchedAsync(playlistId, new PlaylistMetadataFetched
        {
            PlaylistId = playlistId,
            CorrelationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            OperationKey = "playlist/metadata/page/1",
            OccurredAt = DataBridgeTestHelpers.Now,
            Attempt = 1,
            ProviderPlaylistId = "provider-playlist",
            Title = "Playlist Title",
            TotalItems = 10_000,
            PageStartIndex = 1,
            PageSize = 5_000,
            IsComplete = false,
            NextPageStartIndex = 5_001,
            Entries = []
        });

        var playlist = await db.Playlists.SingleAsync();
        playlist.State.ShouldBe(PlaylistState.PendingMetadata);
        playlist.TotalItems.ShouldBe(10_000);
        playlist.LastScannedAt.ShouldBeNull();
    }

    [Test]
    public async Task WriteStagingEntries_Skips_Existing_Indexes_And_Urls()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new PlaylistsRepository(db, SystemClock.Instance);
        var playlistId = Guid.NewGuid();
        db.PlaylistScanEntries.Add(new PlaylistScanEntryEntity
        {
            PlaylistId = playlistId,
            PlaylistIndex = 1,
            EntryUrl = "https://example.test/one",
            EntryTitle = "one"
        });
        db.PlaylistItems.Add(new PlaylistItemEntity
        {
            PlaylistId = playlistId,
            PlaylistIndex = 2,
            EntryUrl = "https://example.test/two",
            EntryTitle = "two",
            JobId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        await repo.WriteStagingEntriesAsync(playlistId,
        [
            new PlaylistEntry
            {
                PlaylistIndex = 1,
                EntryUrl = "https://example.test/new-index-one",
                EntryTitle = "duplicate index"
            },
            new PlaylistEntry
            {
                PlaylistIndex = 3,
                EntryUrl = "https://example.test/two",
                EntryTitle = "duplicate url"
            },
            new PlaylistEntry
            {
                PlaylistIndex = 4,
                EntryUrl = "https://example.test/four",
                EntryTitle = "new"
            }
        ]);

        var staging = await repo.ListStagingEntriesAsync(playlistId);
        staging.Select(x => (x.PlaylistIndex, x.EntryUrl)).ShouldBe([
            (1, "https://example.test/one"),
            (4, "https://example.test/four")
        ]);
    }

    [Test]
    public async Task FanOutEntry_Creates_Download_Job_Playlist_Item_And_Removes_Staging()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new PlaylistsRepository(db, SystemClock.Instance);
        var playlistId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        db.PlaylistScanEntries.Add(new PlaylistScanEntryEntity
        {
            PlaylistId = playlistId,
            PlaylistIndex = 5,
            EntryUrl = "https://example.test/video",
            EntryTitle = "Video"
        });
        await db.SaveChangesAsync();

        await repo.FanOutEntryAsync(new FanOutEntryRequest(
            PlaylistId: playlistId,
            PlaylistIndex: 5,
            EntryUrl: "https://example.test/video",
            EntryTitle: "Video",
            JobId: jobId,
            CorrelationId: Guid.NewGuid(),
            RequestedBy: "micah",
            StorageKey: "default"));

        (await db.PlaylistScanEntries.CountAsync()).ShouldBe(0);
        var job = await db.DownloadJobs.SingleAsync();
        job.JobId.ShouldBe(jobId);
        job.State.ShouldBe(DownloadJobState.Queued);
        job.SourceUrl.ShouldBe("https://example.test/video");

        var item = await db.PlaylistItems.SingleAsync();
        item.PlaylistId.ShouldBe(playlistId);
        item.JobId.ShouldBe(jobId);
        item.PlaylistIndex.ShouldBe(5);
    }

    [Test]
    public async Task List_And_GetDetail_Classify_Item_State_Counts()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new PlaylistsRepository(db, SystemClock.Instance);
        var playlistId = Guid.NewGuid();
        var completedJobId = Guid.NewGuid();
        var failedJobId = Guid.NewGuid();
        var pendingJobId = Guid.NewGuid();
        db.Playlists.Add(new PlaylistEntity
        {
            PlaylistId = playlistId,
            CorrelationId = Guid.NewGuid(),
            State = PlaylistState.MetadataResolved,
            SourceUrl = "https://example.test/playlist",
            Title = "Playlist"
        });
        db.DownloadJobs.AddRange(
            Job(completedJobId, DownloadJobState.Completed),
            Job(failedJobId, DownloadJobState.FailedPermanent),
            Job(pendingJobId, DownloadJobState.Queued));
        db.PlaylistItems.AddRange(
            Item(playlistId, completedJobId, 1),
            Item(playlistId, failedJobId, 2),
            Item(playlistId, pendingJobId, 3));
        db.MediaPlaylistMemberships.Add(new MediaPlaylistMembershipEntity
        {
            PlaylistId = playlistId,
            PlaylistIndex = 1,
            MediaGuid = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var summary = (await repo.ListAsync(pageSize: 1000, pageOffset: -10)).Single();
        var detail = await repo.GetDetailAsync(playlistId);

        summary.CompletedItems.ShouldBe(1);
        summary.FailedItems.ShouldBe(1);
        summary.PendingItems.ShouldBe(1);

        detail.ShouldNotBeNull();
        detail.CompletedItems.ShouldBe(1);
        detail.FailedItems.ShouldBe(1);
        detail.PendingItems.ShouldBe(1);
        detail.Items.Select(x => x.PlaylistIndex).ShouldBe([1, 2, 3]);
        detail.Items.First().MediaGuid.ShouldNotBeNull();
    }

    private static PlaylistRequested PlaylistRequested(
        string sourceUrl,
        string? requestedBy,
        string? storageKey)
        => new()
        {
            PlaylistId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            OperationKey = $"playlist/{Guid.NewGuid():N}/requested",
            OccurredAt = DataBridgeTestHelpers.Now,
            Attempt = 1,
            SourceUrl = sourceUrl,
            RequestedBy = requestedBy,
            StorageKey = storageKey
        };

    private static DownloadJobEntity Job(Guid jobId, DownloadJobState state)
        => new()
        {
            JobId = jobId,
            CorrelationId = Guid.NewGuid(),
            State = state,
            SourceUrl = $"https://example.test/{jobId:N}"
        };

    private static PlaylistItemEntity Item(Guid playlistId, Guid jobId, int index)
        => new()
        {
            PlaylistId = playlistId,
            JobId = jobId,
            PlaylistIndex = index,
            EntryUrl = $"https://example.test/{index}",
            EntryTitle = $"Video {index}"
        };
}
