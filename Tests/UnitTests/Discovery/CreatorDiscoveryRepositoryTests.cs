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
        var source = await repo.CreateSourceAsync(CreateSource());
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
    public async Task UpsertDiscoveredMediaBatch_Does_Not_Enqueue_Unchanged_Known_Candidates()
    {
        await using var db = CreateDb();
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = await repo.CreateSourceAsync(CreateSource());
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
        var source = await repo.CreateSourceAsync(CreateSource());
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
