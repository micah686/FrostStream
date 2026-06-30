using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Downloads;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Discovery;

public sealed class CreatorDiscoveryIgnoreKeywordTests
{
    private const string Owner = "user-1";
    private const string ConfigKey = "default";

    [Test]
    public async Task Full_Scan_Ignores_Candidate_Matching_Config_Set_Keyword()
    {
        await using var db = CreateDb();
        await SeedConfigSetAsync(db, new IgnoreKeyword { Pattern = "trailer" });
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = await repo.CreateSourceAsync(CreateSource());

        var result = await repo.UpsertDiscoveredMediaBatchAsync(FullBatch(
            source.Id,
            Candidate("v1", "Official Trailer"),
            Candidate("v2", "Full Episode")));

        result.NewCount.ShouldBe(2);
        result.EnqueuedItems.Select(x => x.ExternalMediaId).ShouldBe(["v2"]);

        var ignored = await db.DiscoveredMedia.SingleAsync(x => x.ExternalMediaId == "v1");
        ignored.DiscoveryStatus.ShouldBe(MediaDiscoveryStatus.Ignored);
        ignored.IgnoredKeyword.ShouldBe("trailer");

        var queued = await db.DiscoveredMedia.SingleAsync(x => x.ExternalMediaId == "v2");
        queued.DiscoveryStatus.ShouldBe(MediaDiscoveryStatus.Queued);
        queued.IgnoredKeyword.ShouldBeNull();
    }

    [Test]
    public async Task Incremental_Scan_Never_Filters_By_Keyword()
    {
        await using var db = CreateDb();
        await SeedConfigSetAsync(db, new IgnoreKeyword { Pattern = "trailer" });
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = await repo.CreateSourceAsync(CreateSource());

        // Incremental (background) scans carry no requesting user / config set.
        var result = await repo.UpsertDiscoveredMediaBatchAsync(new UpsertDiscoveredMediaBatchRequestMessage
        {
            CreatorSourceId = source.Id,
            ScanMode = CreatorSourceScanMode.Incremental,
            ScheduleKey = "channel-update",
            IdempotencyKey = "channel-update:1",
            ScannedAt = SystemClock.Instance.GetCurrentInstant(),
            Items = [Candidate("v1", "Official Trailer")]
        });

        result.EnqueuedItems.Count.ShouldBe(1);
        (await db.DiscoveredMedia.SingleAsync()).DiscoveryStatus.ShouldBe(MediaDiscoveryStatus.Queued);
    }

    [Test]
    public async Task Ignored_Candidate_Stays_Ignored_On_Subsequent_Full_Scan()
    {
        await using var db = CreateDb();
        await SeedConfigSetAsync(db, new IgnoreKeyword { Pattern = "trailer" });
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = await repo.CreateSourceAsync(CreateSource());
        await repo.UpsertDiscoveredMediaBatchAsync(FullBatch(source.Id, Candidate("v1", "Official Trailer")));

        // Even after the keyword is removed, the row stays ignored until force-queued.
        var configSet = await db.DownloadConfigSets.SingleAsync();
        configSet.IgnoreKeywordsJson = null;
        await db.SaveChangesAsync();

        var result = await repo.UpsertDiscoveredMediaBatchAsync(FullBatch(source.Id, Candidate("v1", "Official Trailer")));

        result.EnqueuedItems.ShouldBeEmpty();
        (await db.DiscoveredMedia.SingleAsync()).DiscoveryStatus.ShouldBe(MediaDiscoveryStatus.Ignored);
    }

    [Test]
    public async Task RequeueIgnoredMedia_Clears_Ignored_State()
    {
        await using var db = CreateDb();
        await SeedConfigSetAsync(db, new IgnoreKeyword { Pattern = "trailer" });
        var repo = new CreatorDiscoveryRepository(db, SystemClock.Instance);
        var source = await repo.CreateSourceAsync(CreateSource());
        await repo.UpsertDiscoveredMediaBatchAsync(FullBatch(source.Id, Candidate("v1", "Official Trailer")));
        var ignored = await db.DiscoveredMedia.SingleAsync();

        var requeued = await repo.RequeueIgnoredMediaAsync(ignored.Id);

        requeued.ShouldNotBeNull();
        requeued!.DiscoveryStatus.ShouldBe(MediaDiscoveryStatus.Queued);
        requeued.IgnoredKeyword.ShouldBeNull();
        (await repo.RequeueIgnoredMediaAsync(999_999)).ShouldBeNull();
    }

    private static async Task SeedConfigSetAsync(DataBridgeDbContext db, params IgnoreKeyword[] keywords)
    {
        db.DownloadConfigSets.Add(new DownloadConfigSetEntity
        {
            OwnerSubject = Owner,
            Key = ConfigKey,
            Name = "Default",
            IgnoreKeywordsJson = IgnoreKeywordMatcher.Serialize(keywords)
        });
        await db.SaveChangesAsync();
    }

    private static UpsertDiscoveredMediaBatchRequestMessage FullBatch(long sourceId, params DiscoveredMediaCandidate[] candidates)
        => new()
        {
            CreatorSourceId = sourceId,
            ScanMode = CreatorSourceScanMode.Full,
            ScheduleKey = "manual-channel-download",
            IdempotencyKey = $"manual:{sourceId}",
            ScannedAt = SystemClock.Instance.GetCurrentInstant(),
            RequestedBy = Owner,
            ConfigSetKey = ConfigKey,
            Items = candidates
        };

    private static DataBridgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<DataBridgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static CreatorSourceEntity CreateSource() => new()
    {
        Platform = "YouTube",
        SourceType = CreatorSourceType.Videos,
        SourceUrl = "https://www.youtube.com/@SomeCreator/videos"
    };

    private static DiscoveredMediaCandidate Candidate(string externalMediaId, string title) => new()
    {
        Platform = "YouTube",
        Extractor = "youtube",
        ExternalMediaId = externalMediaId,
        CanonicalUrl = $"https://www.youtube.com/watch?v={externalMediaId}",
        Title = title
    };
}
