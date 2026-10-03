using System.Data.Common;
using DataBridge;
using DataBridge.Data;
using DataBridge.Metadata;
using DataBridge.MediaStream;
using DataBridge.AudioRenditions;
using DataBridge.Renditions;
using Microsoft.Extensions.Logging.Abstractions;
using DataBridge.Messaging;
using DataBridge.Persistence;
using DataBridge.Persistence.Queries;
using System.Text.RegularExpressions;
using DataBridge.Search;
using DataBridge.Statistics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using NSubstitute;
using FrostStream.ApplicationContracts;
using Shared.Database;
using Shared.Metadata;
using Shared.Storage;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

/// <summary>Every case uses a real SQLite file; an explicit disposable PostgreSQL server adds the same cases on Full.</summary>
public sealed class CoreRepositoryPersistenceTests
{
    private static readonly SemaphoreSlim ProviderCases = new(4, 4);
    private static readonly Instant Timestamp = Instant.FromUnixTimeTicks(-12345670);

    [Test]
    public Task Metadata_Graph_Replacement_Hydration_Assets_And_Search_Are_Portable() => Both(async f =>
    {
        var id = Guid.NewGuid();
        f.Db.Media.Add(new MediaEntity { MediaGuid = id });
        await f.Db.SaveChangesAsync();
        var writer = new MetadataRepository(f.Db);
        var captured = Capture();
        await writer.WriteMetadataAsync(id, captured, "default");
        var reader = new MetadataReadService(f.Database);
        var detail = (await reader.GetDetailAsync(id)).ShouldNotBeNull();
        detail.MediaGuid.ShouldBe(id);
        detail.MetadataScrapedAt.ShouldBe(Timestamp);
        detail.ReleaseDate.ShouldBe(Timestamp);
        detail.Tags.ShouldBe(["100%", "Alpha", "Zulu"]);
        detail.CaptionLanguages.Count.ShouldBe(2);
        detail.Series!.SeriesName.ShouldBe("Series");
        detail.Music!.AlbumTitle.ShouldBe("Album");
        var account = detail.Account;
        (await writer.UpsertAccountAssetsAsync("test", "channel", "inferior name", "https://test", "avatar.png", "banner.png", "default")).ShouldBe(account.AccountId);
        (await reader.GetAccountAsync(account.AccountId)).ShouldNotBeNull().AccountName.ShouldBe("Channel");
        (await reader.ListAccountsAsync(1, null, "test")).Items.Count.ShouldBe(1);
        (await reader.ListTaxonomyAsync(MetadataTaxonomyKind.Tags, 10, 0, "a")).Items.Single().Name.ShouldBe("Alpha");
        (await reader.GetRandomMediaGuidAsync(null)).ShouldBe(id);
        (await reader.GetRandomMediaGuidAsync(id)).ShouldBeNull();
        var technical = (await reader.GetTechnicalAsync(id)).ShouldNotBeNull();
        technical.Streams.Count.ShouldBe(2);
        technical.Chapters.Single().Title.ShouldBe("Intro");
        var documents = new MediaDocumentQuery(f.Database);
        var doc = (await documents.GetMediaByGuidAsync(id)).ShouldNotBeNull();
        doc.Tags.ShouldBe(["100%", "Alpha", "Zulu"]);
        (await documents.GetMediaByGuidsAsync([id, Guid.NewGuid()])).Count.ShouldBe(1);
        (await documents.GetMediaByGuidsAsync([])).ShouldBeEmpty();
        (await documents.GetCommentsByMediaGuidAsync(id)).Single().CommentTimestampUnix.ShouldBe(-1);
        (await documents.GetCaptionsByMediaGuidAsync(id)).First().Id.ShouldStartWith(id.ToString("D") + ":");
        (await documents.GetCaptionsByMediaGuidAsync(id)).Select(c => c.CaptionType).ShouldBe(["subtitles", "automatic_captions"]);
        (await documents.GetMediaBatchAsync(0, 10)).Documents.Count.ShouldBe(1);
        await writer.WriteMetadataAsync(id, captured with { Tags = [], Captions = [], Comments = [] }, "default");
        detail = (await reader.GetDetailAsync(id)).ShouldNotBeNull();
        detail.Tags.ShouldBeEmpty();
        detail.CaptionLanguages.ShouldBeEmpty();
        (await documents.GetCommentsByMediaGuidAsync(id)).ShouldBeEmpty();
        // A late child failure must roll back the earlier metadata deletion and replacement.
        await Should.ThrowAsync<DbException>(() => writer.WriteMetadataAsync(id,
            captured with { Media = captured.Media with { Title = "must roll back" }, Captions = [new() { StoragePath = "bad.vtt", LanguageCode = "en", CaptionType = "invalid" }] }, "default"));
        (await reader.GetDetailAsync(id)).ShouldNotBeNull().Title.ShouldBe("Portable media");
    });

    [Test]
    public Task Notes_Search_Escapes_Wildcards_And_Preserves_Owner_And_Channel_Identity() => Both(async f =>
    {
        var id = await f.SeedMetadata();
        var account = (await new MetadataReadService(f.Database).GetDetailAsync(id))!.Account.AccountId;
        var notes = new UserNotesRepository(f.Db, new FixedClock(Timestamp));
        (await notes.UpsertAsync("alice", "video", id.ToString(), "Mixed CASE 100% under_score \\ café")).Success.ShouldBeTrue();
        (await notes.UpsertAsync("bob", "video", id.ToString(), "Mixed CASE 100%")).Success.ShouldBeTrue();
        (await notes.UpsertAsync("alice", "channel", account.ToString(), "Channel note")).Success.ShouldBeTrue();
        (await notes.SearchAsync("alice", "mixed case", null, 10, 0)).TotalCount.ShouldBe(1);
        (await notes.SearchAsync("alice", "CAFÉ", null, 10, 0)).TotalCount.ShouldBe(1);
        (await notes.SearchAsync("alice", "100%", null, 10, 0)).Items.Count.ShouldBe(1);
        (await notes.SearchAsync("alice", "under_", null, 10, 0)).Items.Count.ShouldBe(1);
        (await notes.SearchAsync("alice", "missing%", null, 10, 0)).Items.ShouldBeEmpty();
        (await notes.GetVideoNotesAsync("alice", [id])).Count.ShouldBe(1);
        (await notes.DeleteAsync("alice", "video", id.ToString())).ShouldBeTrue();
        (await notes.GetAsync("bob", "video", id.ToString())).ShouldNotBeNull();
    });

    [Test]
    public Task Policy_Upserts_Replace_Children_Increment_Version_And_Bind_Empty_Arrays() => Both(async f =>
    {
        var media = await f.SeedMetadata();
        var policies = new AccessPolicyExecutor(f.Database);
        var policy = new AccessPolicyDto
        {
            PolicyId = Guid.NewGuid(), Name = "Portable policy", Enabled = true,
            MediaGuids = [media], Providers = ["test"], AgeThresholds = [18],
            Assignments = [new() { Type = "group", Id = "editors" }]
        };
        var saved = await policies.SaveAsync(policy, default);
        saved.Version.ShouldBe(1);
        saved.MediaGuids.ShouldBe([media]);
        var second = await policies.SaveAsync(policy with { Providers = [], AgeThresholds = [21] }, default);
        second.Version.ShouldBe(2);
        second.Providers.ShouldBeEmpty();
        var conflict = await Should.ThrowAsync<DbException>(() => policies.SaveAsync(policy with { PolicyId = Guid.NewGuid(), Name = "portable policy" }, default));
        ApplicationDatabaseErrors.IsUniqueViolation(conflict).ShouldBeTrue();
        (await policies.SetSyncAsync(policy.PolicyId, 1, AccessPolicySyncStatus.Synced, null, default))!.SyncStatus.ShouldBe(AccessPolicySyncStatus.Pending);
        (await policies.SetSyncAsync(policy.PolicyId, 2, AccessPolicySyncStatus.Synced, null, default))!.SyncStatus.ShouldBe(AccessPolicySyncStatus.Synced);
        (await policies.GetMediaSummaryAsync(media, default)).Found.ShouldBeTrue();
        (await policies.GetMediaSummaryAsync(Guid.NewGuid(), default)).Providers.ShouldBeEmpty();
        await policies.EvaluateAsync(media, "nobody", [], [], default);
        (await policies.EvaluateAsync(media, "nobody", ["EDITORS"], [], default)).IsAllowed.ShouldBeFalse();
        (await policies.DeleteAsync(policy.PolicyId, default)).ShouldBeTrue();
        (await policies.ListAsync(default)).ShouldBeEmpty();
    });

    [Test]
    public Task Deduplication_Circuits_And_Ordinary_Playlist_Transactions_Are_Portable() => Both(async f =>
    {
        var first = await f.SeedMetadata();
        var second = Guid.NewGuid();
        f.Db.Media.Add(new MediaEntity { MediaGuid = second });
        await f.Db.SaveChangesAsync();
        var jobs = new DownloadJobsRepository(f.Db, new FixedClock(Timestamp));
        var message = Guid.NewGuid();
        var job = Guid.NewGuid();
        (await jobs.TryMarkMessageProcessedAsync(message, "test", job)).ShouldBeTrue();
        (await jobs.TryMarkMessageProcessedAsync(message, "test", job)).ShouldBeFalse();
        (await jobs.IsMessageProcessedAsync(message)).ShouldBeTrue();
        var flows = new DownloadFlowV2Repository(f.Db, new FixedClock(Timestamp), NullDownloadJobStateNotifier.Instance);
        await flows.OpenProviderCircuitAsync("example.com", "reason");
        (await flows.FindOpenProviderCircuitAsync("https://example.com/media")).ShouldBe("example.com");
        await flows.ClearProviderCircuitAsync("example.com");
        (await flows.FindOpenProviderCircuitAsync("https://example.com/media")).ShouldBeNull();
        var playlists = new UserPlaylistsRepository(f.Db, new FixedClock(Timestamp));
        var playlist = await playlists.CreateAsync("alice", "Playlist", null);
        var listId = playlist.Playlist.PlaylistId;
        (await playlists.AddItemAsync("alice", listId, first, null)).Success.ShouldBeTrue();
        (await playlists.AddItemAsync("alice", listId, second, 0)).Success.ShouldBeTrue();
        var items = (await playlists.GetAsync("alice", listId))!.Items;
        items.Select(item => item.MediaGuid).ShouldBe([second, first]);
        (await playlists.RemoveItemAsync("alice", listId, second)).Success.ShouldBeTrue();
        (await playlists.GetAsync("alice", listId))!.Items.Single().Position.ShouldBe(1);
    });

    [Test]
    public Task Statistics_Atomic_Increments_Latest_Hydration_And_UTC_Buckets_Are_Portable() => Both(async f =>
    {
        var media = await f.SeedMetadata();
        var at = Instant.FromUtc(2026, 1, 31, 23, 55);
        await DownloadStatisticsRecorder.RecordDailyActivityAsync(f.Db, null, "completed", at, 25, 12.5, default);
        await DownloadStatisticsRecorder.RecordDailyActivityAsync(f.Db, null, "completed", at, 75, 5, default);
        await DownloadStatisticsRecorder.RecordChannelDailyStatesAsync(f.Db, null, "https://test/media", "completed", at, default);
        var stats = new StatisticsReadService(f.Database);
        var buckets = await stats.GetDownloadHistoryAsync(new() { From = at, To = at, Bucket = "day" });
        buckets.Single().Completed.ShouldBe(2);
        buckets.Single().BytesCompleted.ShouldBe(100);
        buckets.Single().DurationCompletedSeconds.ShouldBe(17.5);
        buckets.Single().BucketStart.ShouldBe(Instant.FromUtc(2026, 1, 31, 0, 0));
        var beforeEpoch = Instant.FromUnixTimeTicks(-10);
        await DownloadStatisticsRecorder.RecordDailyActivityAsync(f.Db, null, "created", beforeEpoch, 0, 0, default);
        var negativeBucket = (await stats.GetDownloadHistoryAsync(new() { From = beforeEpoch, To = beforeEpoch, Bucket = "day" })).Single();
        negativeBucket.BucketStart.ShouldBe(Instant.FromUtc(1969, 12, 31, 0, 0));
        negativeBucket.Created.ShouldBe(1);
        foreach (var bucket in new[] { "week", "month" })
            (await stats.GetDownloadHistoryAsync(new() { From = at, To = Instant.FromUtc(2026, 3, 31, 10, 0), Bucket = bucket })).Count.ShouldBeGreaterThan(1);
        (await stats.ListChannelsAsync(10, 0, "name", "asc", "channel")).Items.Count.ShouldBe(1);
        (await stats.SuggestChannelsAsync("channel", 10)).Count.ShouldBe(1);
        var account = (await new MetadataReadService(f.Database).GetDetailAsync(media))!.Account.AccountId;
        (await stats.GetChannelByAccountAsync(account)).ShouldNotBeNull();
        var source = new CreatorSourceEntity { Platform = "test", SourceUrl = "https://test/channel", SourceType = CreatorSourceType.Videos, AccountId = account };
        f.Db.CreatorSources.Add(source);
        await f.Db.SaveChangesAsync();
        f.Db.DiscoveredMedia.Add(new() { CreatorSourceId = source.Id, Platform = "test", Extractor = "test", ExternalMediaId = "external", CanonicalUrl = "https://test/media", DurationSeconds = 42 });
        f.Db.CreatorScanStates.Add(new() { CreatorSourceId = source.Id, LastSuccessfulScanAt = at });
        await f.Db.SaveChangesAsync();
        await DownloadStatisticsRecorder.RecordChannelDailyStatesAsync(f.Db, null, "https://test/media", "completed", at, default);
        var channel = (await stats.GetChannelAsync(source.Id)).ShouldNotBeNull();
        channel.Summary.AccountId.ShouldBe(account);
        channel.Summary.AvailableCount.ShouldBe(1);
        (await stats.GetChannelByAccountAsync(account))!.Summary.CreatorSourceId.ShouldBe(source.Id);
        await stats.GetOverviewAsync("alice");
        await stats.GetCoverageSummaryAsync();
    });

    [Test]
    public Task Assets_Captions_Thumbnails_And_Rendition_Latest_Version_Selection_Are_Portable() => Both(async f =>
    {
        var media = await f.SeedMetadata();
        var metadata = new MetadataReadService(f.Database);
        var account = (await metadata.GetDetailAsync(media))!.Account.AccountId;
        var writer = new MetadataRepository(f.Db);
        await writer.UpdateAccountAssetsByIdAsync(account, "avatar.png", "banner.png", "default");
        (await new AccountAssetReadService(f.Database).ResolveAsync(account, AccountAssetType.Banner))!.StoragePath.ShouldBe("banner.png");
        var captions = new MediaCaptionReadService(f.Database);
        (await captions.ListAsync(media)).Count.ShouldBe(2);
        (await captions.ResolveAsync(media, "en", null))!.CaptionType.ShouldBe("subtitles");
        (await captions.ResolveAsync(media, "en", "automatic_captions"))!.StoragePath.ShouldBe("auto.vtt");
        foreach (var version in new[] { 1, 2 })
            f.Db.MediaContentIdVersions.Add(new() { MediaGuid = media, VersionNum = version, StorageKey = "default", StoragePath = $"version{version}.mp4", ContentHashXxh128 = new string((char)('a' + version), 32) });
        f.Db.AudioRenditions.Add(new() { RenditionId = Guid.NewGuid(), MediaGuid = media, SourceVersionNum = 2, StorageKey = "default", Status = AudioRenditionStatus.Pending });
        await f.Db.SaveChangesAsync();
        var thumbnails = new MediaThumbnailGenerationService(f.Database);
        (await thumbnails.ListMissingAsync(account, null, 10)).Single().StoragePath.ShouldBe("version2.mp4");
        (await thumbnails.CompleteAsync(media, "default", "thumbnail.jpg")).ShouldBeTrue();
        (await thumbnails.CompleteAsync(media, "default", "replacement.jpg")).ShouldBeFalse();
        (await new MediaThumbnailReadService(f.Database).ResolveAsync(media))!.StoragePath.ShouldBe("thumbnail.jpg");
        var encoded = new MediaEncodingStatusRepository(f.Db, f.Database, new FixedClock(Timestamp));
        (await encoded.SetAsync(account, media, true, "default", "audio.opus"))!.IsEncoded.ShouldBeTrue();
        var page = await encoded.ListChannelAsync(account, true, "default", 10, null);
        page.EncodedCount.ShouldBe(1);
        page.Items.Single().MediaGuid.ShouldBe(media);
        var audio = new AudioRenditionRepository(f.Db, f.Database, new FixedClock(Timestamp));
        (await audio.ResolveChannelAsync(account, "default", false, false, false))!.Channel.Items.Count.ShouldBe(1);
        var queue = new RenditionQueueRepository(f.Database);
        (await queue.QueryAsync(new() { Query = media.ToString("D"), Status = "Pending" })).Items.Single().MediaGuid.ShouldBe(media);
        (await metadata.ListVersionsAsync(media)).Select(v => v.VersionNum).ShouldBe([1, 2]);
    });

    [Test]
    public Task Download_Creation_Enum_Json_Guid_Search_And_Redelivery_Are_Portable() => Both(async f =>
    {
        var flows = new DownloadFlowV2Repository(f.Db, new FixedClock(Timestamp), NullDownloadJobStateNotifier.Instance);
        var request = new DownloadRequested { JobId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), MessageId = Guid.NewGuid(), OperationKey = "create", OccurredAt = Timestamp, SourceUrl = "https://test/100%", StorageKey = "default", RequestedBy = "alice" };
        (await flows.CreateInitialRunAsync(request, true)).ShouldNotBeNull();
        (await flows.CreateInitialRunAsync(request, true)).ShouldBeNull();
        f.Db.ChangeTracker.Clear();
        var job = await f.Db.DownloadJobs.SingleAsync();
        job.Status.ShouldBe(DownloadJobStatus.Running);
        job.Stage.ShouldBe(DownloadStage.Metadata);
        job.UpdatedAt.ShouldBe(Timestamp);
        var history = await f.Db.DownloadJobHistory.SingleAsync();
        System.Text.Json.JsonDocument.Parse(history.PayloadJson.ShouldNotBeNull()).RootElement.GetProperty("JobId").GetGuid().ShouldBe(request.JobId);
        var jobs = new DownloadJobsRepository(f.Db, new FixedClock(Timestamp));
        (await jobs.QueryQueueAsync(new() { Query = request.JobId.ToString("D") })).Items.Single().JobId.ShouldBe(request.JobId);
        (await jobs.QueryQueueAsync(new() { Query = "100%" })).Items.Count.ShouldBe(1);
        (await jobs.QueryQueueAsync(new() { Query = "no_match%" })).Items.ShouldBeEmpty();
        // Ordinary transactional read/modify/write; racing mutations remain the 2d acceptance work.
        (await flows.StartFreshRunAsync(Guid.NewGuid())).ShouldBeNull();
    });

    [Test]
    public Task Import_Enum_Json_Null_And_Search_Mappings_Are_Portable() => Both(async f =>
    {
        var session = new ImportSessionEntity { SessionId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), SourceRoot = "/imports", StorageKey = "default", Status = ImportSessionStatus.Reviewing };
        f.Db.ImportSessions.Add(session);
        f.Db.ImportSessionItems.Add(new() { ItemId = Guid.NewGuid(), SessionId = session.SessionId, RelativePath = "folder/MixedCase.mp4", FileName = "MixedCase.mp4", FileMtime = Timestamp, SidecarsJson = "{}", CaptionStoragePathsJson = "[]", UserMetadataJson = "{\"title\":\"Edited\"}" });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var repository = new ImportSessionRepository(f.Db, new FixedClock(Timestamp));
        var result = await repository.ListItemsAsync(new() { SessionId = session.SessionId, Search = "mixedcase" });
        result.TotalCount.ShouldBe(1);
        var item = await f.Db.ImportSessionItems.SingleAsync();
        item.FileMtime.ShouldBe(Timestamp);
        item.SidecarsJson.ShouldBe("{}");
        item.CaptionStoragePathsJson.ShouldBe("[]");
        item.ProbeMetadataJson.ShouldBeNull();
        System.Text.Json.JsonDocument.Parse(item.UserMetadataJson!).RootElement.GetProperty("title").GetString().ShouldBe("Edited");
    });

    [Test]
    public Task Deletion_Checks_Active_Jobs_Then_Removes_Copies_Metadata_And_Search() => Both(async f =>
    {
        var media = await f.SeedMetadata();
        f.Db.StorageConfigs.Add(new() { Key = "alternate" });
        f.Db.MediaContentIdVersions.Add(new() { MediaGuid = media, VersionNum = 1, StorageKey = "default", StoragePath = "first.mp4", ContentHashXxh128 = new string('a', 32) });
        f.Db.MediaContentIdVersions.Add(new() { MediaGuid = media, VersionNum = 2, StorageKey = "alternate", StoragePath = "second.mp4", ContentHashXxh128 = new string('b', 32) });
        var job = new DownloadJobEntity { JobId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), SourceUrl = "https://test/media", State = DownloadJobState.DownloadPending };
        f.Db.DownloadJobs.Add(job);
        f.Db.MediaSourceVersions.Add(new() { MediaGuid = media, LatestJobId = job.JobId, Provider = "test", SourceMediaId = "external" });
        await f.Db.SaveChangesAsync();
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(Arg.Any<string>(), Arg.Any<DeleteMediaFileRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new DeleteMediaFileResponse { Success = true });
        var search = Substitute.For<ITypesenseIndexService>();
        var executor = new MediaDeleteExecutor(f.Database, bus, search, f.ScopeFactory, NullLogger<MediaDeleteExecutor>.Instance);
        (await executor.DeleteMediaAsync(Guid.NewGuid())).ErrorCode.ShouldBe("not_found");
        (await executor.DeleteMediaAsync(media)).ErrorCode.ShouldBe("conflict");
        job.State = DownloadJobState.Completed;
        await f.Db.SaveChangesAsync();
        var partial = await executor.DeleteMediaForStorageKeyAsync(media, "alternate");
        partial.Success.ShouldBeTrue();
        partial.MediaRemoved.ShouldBeFalse();
        partial.FilesDeleted.ShouldBe(1);
        (await f.Db.MediaContentIdVersions.AsNoTracking().CountAsync()).ShouldBe(1);
        (await executor.DeleteMediaForStorageKeyAsync(media, "default")).MediaRemoved.ShouldBeTrue();
        await search.Received(1).DeleteMediaByGuidAsync(media.ToString(), Arg.Any<CancellationToken>());
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(0);
        (await new MetadataReadService(f.Database).GetDetailAsync(media)).ShouldBeNull();
    });

    [Test]
    public Task Watch_And_Likes_Upserts_Preserve_Owner_Timestamps_And_Hydrate_Metadata() => Both(async f =>
    {
        var media = await f.SeedMetadata();
        var bus = new UnitTests.Storage.FakeMessageBus();
        using var service = new WatchStateConsumerService(bus, f.Database, new FixedClock(Timestamp), NullLogger<WatchStateConsumerService>.Instance);
        await service.StartAsync(default);
        try
        {
            for (var attempt = 0; bus.Subscriptions.Count < 8 && attempt < 100; attempt++) await Task.Delay(10);
            bus.Subscriptions.Count.ShouldBe(8);
            var request = new WatchStateUpsertRequest { OwnerSubject = "alice", MediaGuid = media, Completed = true, PositionSeconds = 12, DurationSeconds = 42 };
            var response = (await bus.InvokeAsync<WatchStateUpsertRequest, WatchStateResponse>(WatchStateSubjects.Upsert, request)).ShouldNotBeNull();
            response.Success.ShouldBeTrue();
            response.State!.WatchedAt.ShouldBe(Timestamp);
            await bus.InvokeAsync<WatchStateUpsertRequest, WatchStateResponse>(WatchStateSubjects.Upsert, request with { OwnerSubject = "bob" });
            response = (await bus.InvokeAsync<WatchStateUpsertRequest, WatchStateResponse>(WatchStateSubjects.Upsert, request with { Completed = false })).ShouldNotBeNull();
            response.State!.WatchedAt.ShouldBeNull();
            (await bus.InvokeAsync<WatchStateGetRequest, WatchStateResponse>(WatchStateSubjects.Get, new() { OwnerSubject = "bob", MediaGuid = media }))!.State!.Completed.ShouldBeTrue();
            var like = new MediaLikeStateRequest { OwnerSubject = "alice", MediaGuid = media };
            (await bus.InvokeAsync<MediaLikeStateRequest, MediaLikeStateResponse>(MediaLikeSubjects.Like, like))!.State!.LikedAt.ShouldBe(Timestamp);
            (await bus.InvokeAsync<MediaLikeListRequest, MediaLikeListResponse>(MediaLikeSubjects.List, new() { OwnerSubject = "alice" }))!.Items.Count.ShouldBe(1);
            (await bus.InvokeAsync<WatchStateHistoryListRequest, WatchStateHistoryListResponse>(WatchStateSubjects.ListHistory, new() { OwnerSubject = "alice" }))!.Items.Count.ShouldBe(1);
            (await bus.InvokeAsync<MediaLikeStateRequest, MediaLikeStateResponse>(MediaLikeSubjects.Unlike, like))!.Success.ShouldBeTrue();
            (await bus.InvokeAsync<MediaLikeStateRequest, MediaLikeStateResponse>(MediaLikeSubjects.Get, like))!.State!.Liked.ShouldBeFalse();
        }
        finally { await service.StopAsync(default); }
    });

    [Test]
    public Task Search_Predicates_Handle_Unicode_Nulls_Escapes_And_Long_Patterns() => Both(async f =>
    {
        (string? Value, string Pattern, bool? Match)[] cases =
        [
            ("CAFÉ", "café", true), ("😀", "_", true), ("😀x", "_", false),
            ("A\nB", "a_b", true), ("100%_done", "100\\%\\_done", true),
            ("anything", "", false), ("", "%", true), (null, "%", null),
            (new string('a', 4096), new string('a', 4096) + "%", true),
            ("aaaab", "%a%a%b", true), ("aaaac", "%a%a%b", false)
        ];
        foreach (var (value, pattern, match) in cases)
        {
            await using var command = f.Database.CreateCommand(f.Database.Provider == PersistenceProvider.Postgres
                ? "SELECT @value ILIKE @pattern ESCAPE @escape" : "SELECT fs_ilike(@value, @pattern, @escape)");
            command.Parameters.AddWithValue("value", value);
            command.Parameters.AddWithValue("pattern", pattern);
            command.Parameters.AddWithValue("escape", "\\");
            var actual = await command.ExecuteScalarAsync();
            if (match is null) actual.ShouldBe(DBNull.Value);
            else Convert.ToBoolean(actual).ShouldBe(match.Value);
        }
    });

    [Test]
    public Task Ordinary_EF_Repositories_Preserve_Json_Ownership_Schedules_And_Generated_Ids() => Both(async f =>
    {
        var clock = new FixedClock(Timestamp);
        var presets = new OptionPresetsRepository(f.Db, clock);
        var preset = await presets.CreateAsync("portable", "Portable", null, "{}");
        preset.Id.ShouldBeGreaterThan(0);
        (await presets.UpdateAsync("portable", "Updated", null, "{\"quiet\":true}"))!.LastUpdated.ShouldBe(Timestamp);
        System.Text.Json.JsonDocument.Parse((await presets.GetByKeyAsync("portable"))!.YtDlpOptionsJson).RootElement.GetProperty("quiet").GetBoolean().ShouldBeTrue();
        var configs = new DownloadConfigSetsRepository(f.Db, clock);
        var config = new DownloadConfigSetEntity { OwnerSubject = "alice", Key = "portable", Name = "Portable", StorageKey = "default", YtDlpOptionsJson = "{}", IgnoreKeywordsJson = "[]" };
        await configs.CreateAsync(config);
        config.Id.ShouldBeGreaterThan(0);
        (await configs.GetAsync("bob", "portable")).ShouldBeNull();
        (await configs.UpdateAsync(new() { OwnerSubject = "alice", Key = "portable", Name = "Updated", StorageKey = "default", YtDlpOptionsJson = "{}", IgnoreKeywordsJson = "[]" }))!.UpdatedAt.ShouldBe(Timestamp);
        var schedules = new ScheduledTasksRepository(f.Db, clock);
        var schedule = await schedules.CreateAsync(new() { Key = "portable", TaskType = "channel_scan_refresh", IntervalSeconds = 60, Enabled = true });
        schedule.NextDueAt.ShouldBe(Timestamp + Duration.FromSeconds(60));
        await schedules.MarkAttemptAsync("portable", Timestamp);
        (await schedules.MarkSuccessAsync("portable", Timestamp))!.LastRunStatus.ShouldBe(ScheduleRunStatus.Completed);
        (await schedules.ListActiveAsync()).ShouldContain(item => item.Key == "portable");
        var discovery = new CreatorDiscoveryRepository(f.Db, clock);
        var source = new CreatorSourceEntity { Platform = "test", SourceType = CreatorSourceType.Videos, SourceUrl = "https://test/channel" };
        var created = await discovery.CreateOrReuseSourceAsync(source);
        created.Source.Id.ShouldBeGreaterThan(0);
        (await discovery.CreateOrReuseSourceAsync(new() { Platform = "test", SourceType = CreatorSourceType.Videos, SourceUrl = "https://test/channel" })).Source.Id.ShouldBe(created.Source.Id);
        (await discovery.ListSourcesAsync()).Count.ShouldBe(1);
        (await discovery.DeleteSourceAsync(created.Source.Id)).ShouldBeTrue();
        (await schedules.DeleteAsync("portable")).ShouldBeTrue();
        (await configs.DeleteAsync("alice", "portable")).ShouldBeTrue();
        (await presets.DeleteAsync("portable")).ShouldBeTrue();
    });

    [Test]
    public Task Content_Reservation_Reuses_Bytes_Appends_Source_Versions_And_Rolls_Back_New_Identity() => Both(async f =>
    {
        var jobs = new DownloadJobsRepository(f.Db, new FixedClock(Timestamp));
        var request = new VersionReservationRequest { JobId = Guid.NewGuid(), ContentHashXxh128 = new string('a', 32), StorageKey = "default", FileName = "file.mp4", Provider = "test", SourceMediaId = "external", SourceLastModified = Timestamp, LinkSourceToDownloadJob = false };
        var first = await jobs.ReserveVersionAsync(request);
        first.IsNewMediaGuid.ShouldBeTrue();
        first.VersionNum.ShouldBe(1);
        first.StoragePath.ShouldBe($"archives/{first.MediaGuid:N}/v1/file.mp4");
        var duplicate = await jobs.ReserveVersionAsync(request);
        duplicate.MediaGuid.ShouldBe(first.MediaGuid);
        duplicate.ContentAlreadyStored.ShouldBeTrue();
        var second = await jobs.ReserveVersionAsync(request with { ContentHashXxh128 = new string('b', 32) });
        second.MediaGuid.ShouldBe(first.MediaGuid);
        second.VersionNum.ShouldBe(2);
        (await f.Db.MediaSourceVersions.AsNoTracking().SingleAsync()).SourceLastModified.ShouldBe(Timestamp);
        await jobs.DeleteNewMediaGuidAsync(first.MediaGuid, "test", "external");
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(0);
        (await f.Db.MediaContentIdVersions.AsNoTracking().CountAsync()).ShouldBe(0);
        (await f.Db.MediaSourceVersions.AsNoTracking().CountAsync()).ShouldBe(0);
    });

    [Test]
    public Task Cleanup_And_Optional_Live_Chat_Statements_Use_The_Application_Provider() => Both(async f =>
    {
        var live = await f.SeedMetadata();
        var capture = Capture();
        await new MetadataRepository(f.Db).WriteMetadataAsync(live, capture with { Media = capture.Media with { WasLive = true } }, "default");
        f.Db.MediaContentIdVersions.Add(new() { MediaGuid = live, VersionNum = 1, StorageKey = "default", StoragePath = "live.mp4", ContentHashXxh128 = new string('a', 32) });
        var active = Guid.NewGuid();
        f.Db.Media.Add(new() { MediaGuid = active });
        f.Db.Media.Add(new() { MediaGuid = Guid.NewGuid() });
        var job = new DownloadJobEntity { JobId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), SourceUrl = "https://test/live", State = DownloadJobState.Queued };
        f.Db.DownloadJobs.Add(job);
        f.Db.MediaSourceVersions.Add(new() { MediaGuid = active, LatestJobId = job.JobId });
        await f.Db.SaveChangesAsync();
        await using (var command = f.Database.CreateCommand(f.Database.Sql("BackgroundJobConsumerService.HandleDatabaseStaleMediaCleanupAsync.1")))
        {
            DownloadJobStateSql.AddActiveStatesParameter(command);
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(1);
        }
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(2);
        async Task<bool> HasBackfillCandidate(bool force)
        {
            await using var command = f.Database.CreateCommand(f.Database.Sql("LiveChatBackfillConsumerService.LoadCandidatesAsync.1"));
            command.Parameters.AddWithValue("@target_media_guid", ApplicationParameterType.Guid, null);
            command.Parameters.AddWithValue("@force", force);
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync();
        }
        (await HasBackfillCandidate(false)).ShouldBeTrue();
        for (var count = 1; count <= 2; count++)
        {
            await using var command = f.Database.CreateCommand(f.Database.Sql("LiveChatIngestService.UpsertMarkerAsync.1"));
            command.Parameters.AddWithValue("@media_guid", live);
            command.Parameters.AddWithValue("@version_num", 1);
            command.Parameters.AddWithValue("@message_count", (long)count);
            command.Parameters.AddWithValue("@first_offset_ms", 0L);
            command.Parameters.AddWithValue("@last_offset_ms", 100L);
            await command.ExecuteNonQueryAsync();
        }
        (await new MetadataReadService(f.Database).GetDetailAsync(live))!.HasLiveChat.ShouldBeTrue();
        (await HasBackfillCandidate(false)).ShouldBeFalse();
        (await HasBackfillCandidate(true)).ShouldBeTrue();
        await using (var command = f.Database.CreateCommand(f.Database.Sql("LiveChatIngestService.DeleteForMediaAsync.1")))
        {
            command.Parameters.AddWithValue("@media_guid", live);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        (await new MetadataReadService(f.Database).GetDetailAsync(live))!.HasLiveChat.ShouldBeFalse();
    });

    [Test]
    public Task Direct_EF_User_And_Storage_Consumers_Work_With_Selected_Database_Scopes() => Both(async f =>
    {
        var bus = new UnitTests.Storage.FakeMessageBus();
        var clock = new FixedClock(Timestamp);
        using var users = new UserSessionConsumerService(bus, f.ScopeFactory, clock, NullLogger<UserSessionConsumerService>.Instance);
        using var storage = new StorageCrudConsumerService(bus, f.ScopeFactory, new UnitTests.Storage.InMemorySecretStore(), clock, NullLogger<StorageCrudConsumerService>.Instance);
        await users.StartAsync(default);
        await storage.StartAsync(default);
        try
        {
            for (var attempt = 0; (!bus.Subscriptions.ContainsKey(UserSessionSubjects.Upsert) || !bus.Subscriptions.ContainsKey(StorageSubjects.DeleteStorage)) && attempt < 100; attempt++) await Task.Delay(10);
            var request = new UserSessionUpsertRequestMessage { Subject = "alice", DisplayName = "Alice", Groups = [] };
            var first = (await bus.InvokeAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(UserSessionSubjects.Upsert, request)).ShouldNotBeNull();
            first.Success.ShouldBeTrue();
            var second = (await bus.InvokeAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(UserSessionSubjects.Upsert, request with { DisplayName = "Updated" })).ShouldNotBeNull();
            second.UserId.ShouldBe(first.UserId);
            var user = await f.Db.FrostStreamUsers.AsNoTracking().SingleAsync();
            user.DisplayName.ShouldBe("Updated");
            user.LastUpdated.ShouldBe(Timestamp);
            var create = (await bus.InvokeAsync<StorageCreateLocalRequestMessage, StorageOperationResponseMessage>(StorageSubjects.CreateLocalStorage,
                new() { Key = "portable", Parameters = new() { Protocol = LocalStorageProtocol.Local, Path = "/data/portable" } })).ShouldNotBeNull();
            create.Success.ShouldBeTrue();
            create.Entity!.Local!.Path.ShouldBe("/data/portable");
            var update = (await bus.InvokeAsync<StorageUpdateLocalRequestMessage, StorageOperationResponseMessage>(StorageSubjects.UpdateLocalStorage,
                new() { Key = "portable", Parameters = new() { Protocol = LocalStorageProtocol.Local, Path = "/data/updated" } })).ShouldNotBeNull();
            update.Success.ShouldBeTrue();
            update.Entity!.Local!.Path.ShouldBe("/data/updated");
            (await bus.InvokeAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(StorageSubjects.DeleteStorage, new() { Key = "portable" }))!.Success.ShouldBeTrue();
            (await f.Db.StorageConfigs.AsNoTracking().AnyAsync(item => item.Key == "portable")).ShouldBeFalse();
        }
        finally { await users.StopAsync(default); await storage.StopAsync(default); }
    });

    [Test]
    public async Task Sqlite_Query_Catalog_Prepares_Against_The_Application_Baseline()
    {
        await using var fixture = await Fixture.Create(false);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        var cte = fixture.Database.Sql("StatisticsReadService.Fields.1");
        var prepared = 0;
        foreach (var (operation, query) in ApplicationQueryCatalog.Statements)
        {
            object?[] fragments = operation switch
            {
                "MetadataReadService.ListTaxonomyAsync.1" => ["metadata_tags", "tag_name"],
                "MetadataReadService.ListTaxonomyAsync.2" => ["tag_name", "metadata_tags", "metadata_media_tags", "tag_id", "tag_name", "tag_name", "tag_name"],
                "MetadataRepository.UpsertLookupAsync.1" => ["metadata_tags", "tag_name", "tag_name", "tag_name", "tag_name"],
                "AccessPolicyExecutor.SaveAsync.2" => ["access_policy_media"],
                "AccessPolicyExecutor.LoadStringsAsync.1" => ["provider", "access_policy_providers", "provider"],
                "AccessPolicyExecutor.LoadGuidsAsync.1" => ["media_guid", "access_policy_media", "media_guid"],
                "AccessPolicyExecutor.LoadIntsAsync.1" => ["minimum_age", "access_policy_age_tiers", "minimum_age"],
                "AccessPolicyExecutor.InsertValuesAsync.1" => ["access_policy_providers", "provider"],
                "AccountAssetReadService.ResolveAsync.1" => ["avatar_storage_path", "avatar_storage_path"],
                "MediaDocumentQuery.CreateMediaCommand.1" or "MediaDocumentQuery.CreateCommentsCommand.1" or "MediaDocumentQuery.CreateCaptionsCommand.1" => ["WHERE 1=1"],
                "StatisticsReadService.AccountSummarySql.1" or "StatisticsReadService.ChannelSummarySql.1" => [cte, ""],
                "StatisticsReadService.ListChannelsAsync.2" => ["WHERE 1=1", "account_rollup.account_id"],
                "StatisticsReadService.GetCoverageSummaryAsync.1" or "StatisticsReadService.GetCoverageSummaryAsync.2" => ["('Ignored', 'PossiblyUnavailable', 'Unavailable', 'RemovedFromSource')"],
                _ when query.Sqlite.StartsWith("{fs0}", StringComparison.Ordinal) => [cte],
                _ => []
            };
            var sql = ApplicationQueries.Render(PersistenceProvider.Sqlite, operation, fragments);
            // Remaining placeholders in these statements are EF data parameters, not identifiers.
            sql = Regex.Replace(sql, @"\{fs(\d+)\}", "@fs$1");
            if (operation == "StatisticsReadService.Fields.1") sql += "\nSELECT * FROM classified_media";
            // Standalone WHERE/ORDER fragments are validated as part of their composed statements.
            if (sql.TrimStart().StartsWith("WHERE ", StringComparison.Ordinal) || sql.TrimStart().StartsWith("ORDER BY ", StringComparison.Ordinal)) continue;
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var command = ApplicationDbCommands.Create(connection, "EXPLAIN " + statement);
                foreach (var parameter in Regex.Matches(statement, @"@(\w+)").Select(match => match.Value).Distinct())
                    command.Parameters.AddWithValue(parameter, DBNull.Value);
                try
                {
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) { }
                }
                catch (Exception exception) { throw new InvalidOperationException($"SQLite operation '{operation}' does not prepare against the baseline.", exception); }
                prepared++;
            }
        }
        prepared.ShouldBeGreaterThan(120);
    }

    internal static CapturedMediaMetadata Capture() => new()
    {
        Account = new() { Platform = "test", AccountName = "Channel", AccountHandle = "channel", ExternalIds = [new() { Kind = "channel_id", Value = "stable" }] },
        Media = new() { MetadataScrapeDate = Timestamp, ReleaseDate = Timestamp, Title = "Portable media", Availability = "public", DurationSeconds = 42, WebpageUrl = "https://test/media", ExternalMediaId = "external" },
        Tags = ["Zulu", "Alpha", "100%"], Categories = ["Category"], Genres = ["Genre"], Artists = ["Artist"], AlbumArtists = ["Album artist"], Cast = ["Cast"],
        Captions = [new() { StoragePath = "auto.vtt", LanguageCode = "en", CaptionType = "automatic_captions", Name = "Auto" }, new() { StoragePath = "manual.vtt", LanguageCode = "en", CaptionType = "subtitles", Name = "Manual" }],
        Series = new() { SeriesName = "Series", SeasonNumber = 1, EpisodeNumber = 2, EpisodeName = "Episode" },
        Music = new() { AlbumTitle = "Album", TrackTitle = "Track", TrackNumber = 1 },
        Comments = [new() { CommentId = "comment", Text = "Comment", CommentTimestamp = Timestamp, Account = new() { Platform = "test", AccountName = "Viewer", AccountHandle = "viewer" } }],
        Technical = new()
        {
            Format = new() { FormatLongNames = "mp4", StreamCount = 2 },
            Streams = [new() { StreamType = "video", CodecName = "h264", CodecLongName = "AVC", IsPrimary = true, Video = new() { Width = 1920, Height = 1080 } }, new() { StreamType = "audio", CodecName = "aac", CodecLongName = "AAC", Audio = new() { Channels = 2, SampleRateHz = 48000 } }],
            Chapters = [new() { Title = "Intro", EndTicks = 10 }]
        }
    };

    internal static async Task Both(Func<Fixture, Task> test)
    {
        // Each contention case intentionally opens several independent writer connections. Keep
        // simultaneous cases below a stock disposable PostgreSQL server's connection limit.
        await ProviderCases.WaitAsync();
        try
        {
            await using (var sqlite = await Fixture.Create(false)) await test(sqlite);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES")))
                await using (var postgres = await Fixture.Create(true)) await test(postgres);
        }
        finally { ProviderCases.Release(); }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly IHost host;
        private readonly IServiceScope scope;
        private readonly string? directory;
        private readonly string? adminConnection;
        private readonly string? databaseName;
        public DataBridgeDbContext Db { get; }
        public ApplicationDatabase Database { get; }
        public IServiceScopeFactory ScopeFactory => host.Services.GetRequiredService<IServiceScopeFactory>();
        private Fixture(IHost host, string? directory, string? adminConnection, string? databaseName)
        {
            this.host = host; this.directory = directory; this.adminConnection = adminConnection; this.databaseName = databaseName;
            scope = host.Services.CreateScope();
            Db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            Database = host.Services.GetRequiredService<ApplicationDatabase>();
        }
        public static async Task<Fixture> Create(bool postgres)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            string? dir = null, admin = null, name = null;
            if (postgres)
            {
                admin = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES")!;
                name = "phase2c_" + Guid.NewGuid().ToString("N");
                await using var connection = new NpgsqlConnection(admin);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE {name}";
                await command.ExecuteNonQueryAsync();
                builder.Configuration["ConnectionStrings:froststreamdb"] = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false }.ConnectionString;
            }
            else
            {
                dir = Path.Combine(Path.GetTempPath(), "froststream-core-" + Guid.NewGuid().ToString("N"));
                builder.Configuration["Deployment:Mode"] = "Lite";
                builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
                builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(dir, "core.db");
            }
            builder.AddDataBridgePersistence();
            var host = builder.Build();
            host.InitializeDataBridge();
            return new Fixture(host, dir, admin, name);
        }
        public async Task<Guid> SeedMetadata()
        {
            var id = Guid.NewGuid();
            Db.Media.Add(new MediaEntity { MediaGuid = id });
            await Db.SaveChangesAsync();
            await new MetadataRepository(Db).WriteMetadataAsync(id, Capture(), "default");
            return id;
        }
        public async ValueTask DisposeAsync()
        {
            scope.Dispose(); host.Dispose();
            if (directory is not null) Directory.Delete(directory, true);
            if (adminConnection is not null)
            {
                await using var connection = new NpgsqlConnection(adminConnection);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE {databaseName} WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
