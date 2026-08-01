using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using Shared.Database;
using Shared.Messaging;
using TUnit.Core;

namespace UnitTests.Discovery;

public sealed class CreatorDiscoveryRepositoryTests
{
    [Test]
    public async Task UpsertDiscoveredMediaBatch_Inserts_New_Candidates_For_Enrichment()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;
        var scannedAt = SystemClock.Instance.GetCurrentInstant();

        var result = await repo.UpsertDiscoveredMediaBatchAsync(new UpsertDiscoveredMediaBatchRequestMessage
        {
            CreatorSourceId = source.Id,
            ScanMode = CreatorSourceScanMode.Incremental,
            ScheduleKey = "channel-update",
            IdempotencyKey = "channel-update:1",
            ScannedAt = scannedAt,
            Items =
            [
                Candidate("abc123", "https://www.youtube.com/watch?v=abc123", title: "First media")
            ]
        });

        result.NewCount.ShouldBe(1);
        result.ChangedCount.ShouldBe(0);
        result.EnqueuedItems.Count.ShouldBe(1);

        var row = await db.DiscoveredMedia.SingleAsync();
        row.MetadataStatus.ShouldBe(MediaMetadataStatus.RefreshRequested);
        row.DiscoveryStatus.ShouldBe(MediaDiscoveryStatus.Queued);
        row.FirstSeenAt.ShouldBe(scannedAt);
        row.LastSeenAt.ShouldBe(scannedAt);
    }

    [Test]
    public async Task UpsertDiscoveredMediaBatch_Updates_Source_Platform_From_YtDlp_Candidate()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(new CreatorSourceEntity
        {
            Platform = "unknown",
            SourceUrl = "https://www.youtube.com/@SomeCreator/videos"
        })).Source;

        await repo.UpsertDiscoveredMediaBatchAsync(Batch(source.Id, Candidate("abc123", "https://www.youtube.com/watch?v=abc123", title: "First media")));

        (await repo.GetSourceAsync(source.Id))!.Source.Platform.ShouldBe("YouTube");
    }

    [Test]
    public async Task CreateOrReuseSource_Reuses_Existing_Source_By_Url()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var created = await repo.CreateOrReuseSourceAsync(CreateSource());

        var reused = await repo.CreateOrReuseSourceAsync(new CreatorSourceEntity
        {
            Platform = "youtube",
            SourceType = CreatorSourceType.Streams,
            SourceUrl = created.Source.SourceUrl
        });

        reused.Source.Id.ShouldBe(created.Source.Id);
        reused.Source.SourceType.ShouldBe(CreatorSourceType.Videos);
        (await db.CreatorSources.CountAsync()).ShouldBe(1);
        (await db.CreatorScanStates.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task LinkAccount_Persists_Account_Id_And_Is_Cleared_When_The_Source_Is_Repointed()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;
        source.AccountId.ShouldBeNull();

        await repo.LinkAccountAsync(source.Id, 42);
        (await repo.GetSourceAsync(source.Id))!.Source.AccountId.ShouldBe(42);

        // Re-linking to the same account is a no-op, not a duplicate write.
        await repo.LinkAccountAsync(source.Id, 42);
        (await repo.GetSourceAsync(source.Id))!.Source.AccountId.ShouldBe(42);

        // Repointing the source at a different URL invalidates the derived account.
        var repointed = await repo.UpdateSourceAsync(new CreatorSourceEntity
        {
            Id = source.Id,
            Platform = source.Platform,
            SourceType = source.SourceType,
            SourceUrl = "https://www.youtube.com/@SomeOtherCreator/videos",
            ConfigSetOwnerSubject = "unit_test_user",
            ConfigSetKey = "creator-default"
        });

        repointed.ShouldNotBeNull();
        repointed.Source.AccountId.ShouldBeNull();
        repointed.Source.ConfigSetOwnerSubject.ShouldBe("unit_test_user");
        repointed.Source.ConfigSetKey.ShouldBe("creator-default");
    }

    [Test]
    public async Task UpsertDiscoveredMediaBatch_Does_Not_Enqueue_Unchanged_Known_Candidates()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;
        var candidate = Candidate("abc123", "https://www.youtube.com/watch?v=abc123", title: "First media");
        await repo.UpsertDiscoveredMediaBatchAsync(Batch(source.Id, candidate));

        var result = await repo.UpsertDiscoveredMediaBatchAsync(Batch(source.Id, candidate));

        result.NewCount.ShouldBe(0);
        result.ChangedCount.ShouldBe(0);
        result.EnqueuedItems.ShouldBeEmpty();
        (await db.DiscoveredMedia.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task UpsertDiscoveredMediaBatch_Enqueues_Known_Candidates_When_Lightweight_Metadata_Changes()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;
        await repo.UpsertDiscoveredMediaBatchAsync(Batch(source.Id, Candidate("abc123", "https://www.youtube.com/watch?v=abc123", title: "Old title")));

        var result = await repo.UpsertDiscoveredMediaBatchAsync(Batch(source.Id, Candidate("abc123", "https://www.youtube.com/watch?v=abc123", title: "New title")));

        result.NewCount.ShouldBe(0);
        result.ChangedCount.ShouldBe(1);
        result.EnqueuedItems.Single().Title.ShouldBe("New title");

        var row = await db.DiscoveredMedia.SingleAsync();
        row.Title.ShouldBe("New title");
        row.MetadataStatus.ShouldBe(MediaMetadataStatus.RefreshRequested);
        row.LastChangedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Full_Channel_Download_Queues_Every_Known_Candidate_As_An_Independent_Job_Request()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;
        var candidate = Candidate("abc123", "https://www.youtube.com/watch?v=abc123", title: "First media");
        await repo.UpsertDiscoveredMediaBatchAsync(Batch(source.Id, candidate));

        var request = Batch(source.Id, candidate) with { QueueAllItems = true };
        var result = await repo.UpsertDiscoveredMediaBatchAsync(request);

        result.NewCount.ShouldBe(0);
        result.ChangedCount.ShouldBe(0);
        result.EnqueuedItems.ShouldHaveSingleItem();
    }

    [Test]
    public async Task UpsertDiscoveredMediaBatch_Uses_Scan_High_Watermark_When_Batch_Is_Chunked()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;

        await repo.UpsertDiscoveredMediaBatchAsync(new UpsertDiscoveredMediaBatchRequestMessage
        {
            CreatorSourceId = source.Id,
            ScanMode = CreatorSourceScanMode.Full,
            ScheduleKey = "channel-full",
            IdempotencyKey = "channel-full:1:batch-3",
            ScannedAt = SystemClock.Instance.GetCurrentInstant(),
            ScanHighWatermarkExternalMediaId = "first-in-scan",
            Items =
            [
                Candidate("later-batch-item", "https://www.youtube.com/watch?v=later-batch-item", title: "Later")
            ]
        });

        var updated = await db.CreatorScanStates.SingleAsync();
        updated.LastSeenHighWatermark.ShouldBe("first-in-scan");
    }

    [Test]
    public async Task Full_Scan_Final_Batch_Advances_Cursor_When_Page_Is_Not_Complete()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;

        await repo.UpsertDiscoveredMediaBatchAsync(new UpsertDiscoveredMediaBatchRequestMessage
        {
            CreatorSourceId = source.Id,
            ScanMode = CreatorSourceScanMode.Full,
            ScheduleKey = "channel-full",
            IdempotencyKey = "channel-full:1:batch-49",
            ScannedAt = SystemClock.Instance.GetCurrentInstant(),
            ScanPageStartIndex = 1,
            NextScanPageStartIndex = 5_001,
            ScanPageComplete = false,
            IsScanPageFinalBatch = true,
            Items =
            [
                Candidate("page-item", "https://www.youtube.com/watch?v=page-item", title: "Page")
            ]
        });

        var updated = await db.CreatorScanStates.SingleAsync();
        updated.NextFullScanStartIndex.ShouldBe(5_001);
        updated.LastFullScanAt.ShouldBeNull();
    }

    [Test]
    public async Task Full_Scan_Final_Batch_Clears_Cursor_When_Page_Is_Complete()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = (await repo.CreateSourceAsync(CreateSource())).Source;
        (await db.CreatorScanStates.SingleAsync()).NextFullScanStartIndex = 5_001;
        await db.SaveChangesAsync();

        var scannedAt = SystemClock.Instance.GetCurrentInstant();
        await repo.UpsertDiscoveredMediaBatchAsync(new UpsertDiscoveredMediaBatchRequestMessage
        {
            CreatorSourceId = source.Id,
            ScanMode = CreatorSourceScanMode.Full,
            ScheduleKey = "channel-full",
            IdempotencyKey = "channel-full:1:batch-0",
            ScannedAt = scannedAt,
            ScanPageStartIndex = 5_001,
            ScanPageComplete = true,
            IsScanPageFinalBatch = true,
            Items = []
        });

        var updated = await db.CreatorScanStates.SingleAsync();
        updated.NextFullScanStartIndex.ShouldBeNull();
        updated.LastFullScanAt.ShouldBe(scannedAt);
    }

    private static DataBridgeDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DataBridgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DataBridgeDbContext(options);
    }

    private static CreatorSourceEntity CreateSource() => new()
    {
        Platform = "YouTube",
        SourceType = CreatorSourceType.Videos,
        SourceUrl = "https://www.youtube.com/@SomeCreator/videos"
    };

    private static UpsertDiscoveredMediaBatchRequestMessage Batch(long sourceId, DiscoveredMediaCandidate candidate)
        => new()
        {
            CreatorSourceId = sourceId,
            ScanMode = CreatorSourceScanMode.Incremental,
            ScheduleKey = "channel-update",
            IdempotencyKey = $"channel-update:{sourceId}",
            ScannedAt = SystemClock.Instance.GetCurrentInstant(),
            Items = [candidate]
        };

    private static DiscoveredMediaCandidate Candidate(string externalMediaId, string canonicalUrl, string title)
        => new()
        {
            Platform = "YouTube",
            Extractor = "youtube",
            ExternalMediaId = externalMediaId,
            CanonicalUrl = canonicalUrl,
            Title = title,
            DurationSeconds = 42,
            ThumbnailUrl = "https://example.test/thumb.jpg",
            LiveStatus = "NotLive",
            Availability = "Public"
        };
}
