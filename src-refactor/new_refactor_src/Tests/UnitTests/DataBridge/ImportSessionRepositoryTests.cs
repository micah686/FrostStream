using DataBridge.Data;
using NodaTime;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class ImportSessionRepositoryTests
{
    [Test]
    public async Task Scan_Items_Start_Unselected_And_Can_Be_Included()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());

        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem
            {
                RelativePath = "incoming/video.mp4",
                FileName = "video.mp4",
                MetadataState = ImportSessionItemMetadataState.Incomplete,
                MetadataSource = ImportSessionItemMetadataSource.Placeholder
            }
        ]);

        var available = await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId, Included = false });
        available.TotalCount.ShouldBe(1);
        available.Items[0].Excluded.ShouldBeTrue();
        available.Items[0].MetadataSource.ShouldBe(ImportSessionItemMetadataSource.Placeholder);

        await repository.ApplyBulkAsync(new ImportSessionItemsBulkRequest
        {
            SessionId = sessionId,
            Action = ImportSessionBulkAction.Include,
            ItemIds = [available.Items[0].ItemId]
        });

        var selected = await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId, Included = true });
        selected.TotalCount.ShouldBe(1);
        selected.Items[0].Excluded.ShouldBeFalse();
    }

    [Test]
    public async Task Commit_Uses_Placeholder_As_Automatic_Final_Fallback()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());
        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem
            {
                RelativePath = "video.mp4",
                FileName = "video.mp4",
                Title = "Video",
                MetadataState = ImportSessionItemMetadataState.Incomplete,
                MetadataSource = ImportSessionItemMetadataSource.Placeholder
            }
        ]);
        var item = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        await repository.ApplyBulkAsync(new ImportSessionItemsBulkRequest { SessionId = sessionId, Action = ImportSessionBulkAction.Include, ItemIds = [item.ItemId] });

        var result = await repository.CommitAsync(sessionId);

        result.Error.ShouldBeNull();
        result.ApprovedCount.ShouldBe(1);
        var committed = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        committed.Status.ShouldBe(ImportSessionItemStatus.Approved);
        committed.MetadataState.ShouldBe(ImportSessionItemMetadataState.PlaceholderAccepted);
    }

    [Test]
    public async Task ClaimApprovedWork_Moves_Items_Out_Of_Dispatchable_State()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());
        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem
            {
                RelativePath = "video.mp4",
                FileName = "video.mp4",
                Title = "Video",
                MetadataState = ImportSessionItemMetadataState.Ready,
                MetadataSource = ImportSessionItemMetadataSource.Placeholder
            }
        ]);
        var item = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        await repository.ApplyBulkAsync(new ImportSessionItemsBulkRequest { SessionId = sessionId, Action = ImportSessionBulkAction.Include, ItemIds = [item.ItemId] });
        await repository.CommitAsync(sessionId);

        var claimed = await repository.ClaimApprovedWorkAsync(sessionId, 10);
        var claimedAgain = await repository.ClaimApprovedWorkAsync(sessionId, 10);

        claimed.Count.ShouldBe(1);
        claimed[0].ItemId.ShouldBe(item.ItemId);
        claimed[0].Attempt.ShouldBe(1);
        claimedAgain.Count.ShouldBe(0);
        var updated = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        updated.Status.ShouldBe(ImportSessionItemStatus.Hashing);
        updated.Attempt.ShouldBe(1);
    }

    [Test]
    public async Task RecoverStaleHashingItems_Returns_Expired_Claims_To_Approved()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());
        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem
            {
                RelativePath = "video.mp4",
                FileName = "video.mp4",
                Title = "Video",
                MetadataState = ImportSessionItemMetadataState.Ready,
                MetadataSource = ImportSessionItemMetadataSource.Placeholder
            }
        ]);
        var item = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        await repository.ApplyBulkAsync(new ImportSessionItemsBulkRequest { SessionId = sessionId, Action = ImportSessionBulkAction.Include, ItemIds = [item.ItemId] });
        await repository.CommitAsync(sessionId);
        await repository.ClaimApprovedWorkAsync(sessionId, 10);

        var recoveredEarly = await repository.RecoverStaleHashingItemsAsync(sessionId, DataBridgeTestHelpers.Now.Minus(Duration.FromMinutes(1)));
        var recovered = await repository.RecoverStaleHashingItemsAsync(sessionId, DataBridgeTestHelpers.Now.Plus(Duration.FromMinutes(1)));

        recoveredEarly.ShouldBe(0);
        recovered.ShouldBe(1);
        var updated = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        updated.Status.ShouldBe(ImportSessionItemStatus.Approved);
        updated.Attempt.ShouldBe(1);
    }

    [Test]
    public async Task Ingest_With_YtDlp_InfoJson_Marks_Metadata_Fetched()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());

        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem
            {
                RelativePath = "video.mp4",
                FileName = "video.mp4",
                Title = "Video",
                SidecarsJson = """{"infoJson":"video.info.json"}""",
                MetadataState = ImportSessionItemMetadataState.Ready,
                MetadataSource = ImportSessionItemMetadataSource.YtDlp
            }
        ]);

        var item = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        item.MetadataSource.ShouldBe(ImportSessionItemMetadataSource.YtDlp);
        item.MetadataFetchState.ShouldBe(ImportSessionMetadataFetchState.Succeeded);
        item.MetadataFetchMessage.ShouldBe("info.json found");
    }

    [Test]
    public async Task InfoJson_Refresh_Miss_Returns_To_NotAttempted()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());
        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem
            {
                RelativePath = "video.mp4",
                FileName = "video.mp4",
                MetadataState = ImportSessionItemMetadataState.Incomplete,
                MetadataSource = ImportSessionItemMetadataSource.Placeholder
            }
        ]);
        var item = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        await repository.ApplyBulkAsync(new ImportSessionItemsBulkRequest { SessionId = sessionId, Action = ImportSessionBulkAction.Include, ItemIds = [item.ItemId] });
        await repository.MarkEnrichmentQueuedAsync(sessionId, [item.ItemId]);

        await repository.ApplyEnrichFailureAsync(new ImportSessionItemEnrichFailed
        {
            JobId = sessionId,
            CorrelationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            OperationKey = "test",
            OccurredAt = DataBridgeTestHelpers.Now,
            Attempt = 1,
            SessionId = sessionId,
            ItemId = item.ItemId,
            ErrorCode = "info_json_not_found",
            ErrorMessage = "No adjacent .info.json metadata sidecar was found."
        });

        var refreshed = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items.Single();
        refreshed.MetadataFetchState.ShouldBe(ImportSessionMetadataFetchState.NotAttempted);
        refreshed.MetadataFetchMessage.ShouldBe("No adjacent .info.json metadata sidecar was found.");
        refreshed.ErrorCode.ShouldBeNull();
        refreshed.ErrorMessage.ShouldBeNull();
    }

    [Test]
    public async Task Mapping_Updates_Selected_Files_Including_YtDlp_When_Explicitly_Specified()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repository = new ImportSessionRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var sessionId = Guid.NewGuid();
        await repository.CreateAsync(new ImportSessionCreateRequest { StorageKey = "default" }, sessionId, Guid.NewGuid());
        await repository.IngestScannedItemsAsync(sessionId,
        [
            new ImportSessionScannedItem { RelativePath = "selected.mp4", FileName = "selected.mp4", MetadataState = ImportSessionItemMetadataState.Incomplete, MetadataSource = ImportSessionItemMetadataSource.Placeholder },
            new ImportSessionScannedItem { RelativePath = "excluded.mp4", FileName = "excluded.mp4", MetadataState = ImportSessionItemMetadataState.Incomplete, MetadataSource = ImportSessionItemMetadataSource.Placeholder },
            new ImportSessionScannedItem { RelativePath = "yt-dlp.mp4", FileName = "yt-dlp.mp4", MetadataState = ImportSessionItemMetadataState.Ready, MetadataSource = ImportSessionItemMetadataSource.YtDlp }
        ]);
        var items = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items;
        var selected = items.Single(x => x.FileName == "selected.mp4");
        var ytDlp = items.Single(x => x.FileName == "yt-dlp.mp4");
        await repository.ApplyBulkAsync(new ImportSessionItemsBulkRequest { SessionId = sessionId, Action = ImportSessionBulkAction.Include, ItemIds = [selected.ItemId, ytDlp.ItemId] });

        var result = await repository.ApplyMappingAsync(sessionId,
        [
            new ImportSessionMappingRow { FileName = "selected.mp4", Title = "Mapped" },
            new ImportSessionMappingRow { FileName = "excluded.mp4", Title = "Must not map" },
            new ImportSessionMappingRow { FileName = "yt-dlp.mp4", Title = "Mapped yt-dlp" }
        ], "bucket", "key", "json");

        result.MatchedCount.ShouldBe(2);
        result.UnmatchedCount.ShouldBe(1);
        var updated = (await repository.ListItemsAsync(new ImportSessionItemsListRequest { SessionId = sessionId })).Items;
        updated.Single(x => x.FileName == "selected.mp4").MetadataSource.ShouldBe(ImportSessionItemMetadataSource.ManualMapping);
        updated.Single(x => x.FileName == "excluded.mp4").Title.ShouldBeNull();
        updated.Single(x => x.FileName == "yt-dlp.mp4").Title.ShouldBe("Mapped yt-dlp");
        updated.Single(x => x.FileName == "yt-dlp.mp4").MetadataSource.ShouldBe(ImportSessionItemMetadataSource.ManualMapping);
    }
}
