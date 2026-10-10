using System.Diagnostics;
using System.Diagnostics.Metrics;
using DataBridge.Data;
using DataBridge.Metadata;
using DataBridge.Messaging;
using DataBridge.Search;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging.Abstractions;
using DataBridge.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using NSubstitute;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using static UnitTests.DataBridge.CoreRepositoryPersistenceTests;

namespace UnitTests.DataBridge;

public sealed class BulkContentionPersistenceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 3, 12, 0);
    private static DownloadFlowV2Repository Flow(DataBridgeDbContext db, IDownloadJobStateNotifier? notifier = null)
        => new(db, new FixedClock(Now), notifier ?? NullDownloadJobStateNotifier.Instance);
    private static DownloadRequested Request() => new()
    {
        JobId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), SourceUrl = "https://test/media",
        StorageKey = "default", RequestedBy = "alice", OccurredAt = Now, MessageId = Guid.NewGuid(), OperationKey = "initial"
    };

    private static Task<T[]> Race<T>(Fixture fixture, int writers, Func<DataBridgeDbContext, int, Task<T>> action)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            using var scope = fixture.ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            await ready.Task;
            return await action(db, i);
        })).ToArray();
        ready.SetResult();
        return Task.WhenAll(tasks);
    }

    [Test]
    public Task Concurrent_Redelivery_Creates_One_Run_History_And_Notification() => Both(async f =>
    {
        var request = Request();
        var notifier = Substitute.For<IDownloadJobStateNotifier>();
        var runs = await Race(f, 6, (db, _) => Flow(db, notifier).CreateInitialRunAsync(request, true));
        runs.Count(x => x is not null).ShouldBe(1);
        (await f.Db.DownloadJobs.AsNoTracking().CountAsync()).ShouldBe(1);
        (await f.Db.DownloadJobRuns.AsNoTracking().CountAsync()).ShouldBe(1);
        (await f.Db.DownloadJobHistory.AsNoTracking().CountAsync()).ShouldBe(1);
        notifier.ReceivedCalls().Count(x => x.GetMethodInfo().Name == nameof(IDownloadJobStateNotifier.NotifyV2Async)).ShouldBe(1);
    });

    [Test]
    public Task Concurrent_Start_And_Claims_Have_One_Winner_And_Reject_Stale_Results() => Both(async f =>
    {
        var request = Request();
        await Flow(f.Db).CreateInitialRunAsync(request, false);
        var started = await Race(f, 4, (db, _) => Flow(db).StartFreshRunAsync(request.JobId));
        var run = started.Single(x => x is not null)!;
        var execution = new DownloadExecutionIdentity
        {
            JobId = request.JobId, RunId = run.RunId, CorrelationId = request.CorrelationId, Stage = DownloadStage.Metadata,
            Attempt = 1, DispatchId = Guid.NewGuid()
        };
        (await Flow(f.Db).BeginStageAttemptAsync(execution, "attempt")).ShouldBeTrue();
        var claims = await Race(f, 4, (db, i) => Flow(db).TryAcquireLeaseAsync(new() { Execution = execution, OccurredAt = Now, WorkerInstanceId = "worker" + i }));
        claims.Count(x => x.Granted).ShouldBe(1);
        (await Flow(f.Db).BeginStageAttemptAsync(execution, "attempt")).ShouldBeTrue();
        (await f.Db.DownloadStageAttempts.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadStageStatus.Running);
        var lease = await f.Db.DownloadWorkerLeases.AsNoTracking().SingleAsync();
        (await Flow(f.Db).TryAcquireLeaseAsync(new() { Execution = execution, OccurredAt = Now, WorkerInstanceId = lease.WorkerInstanceId })).Granted.ShouldBeTrue();
        await Flow(f.Db).RequestStopAsync(request.JobId, "alice", "stop");
        (await Flow(f.Db).CompleteStageAttemptAsync(execution)).ShouldBeFalse();
        (await f.Db.DownloadStageAttempts.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadStageStatus.Running);
        await Flow(f.Db).MarkStoppedAsync(request.JobId, run.RunId, null);
        (await Flow(f.Db).StartFreshRunAsync(request.JobId)).ShouldNotBeNull();
        (await Flow(f.Db).CompleteStageAttemptAsync(execution)).ShouldBeFalse();
        (await f.Db.DownloadJobRuns.AsNoTracking().CountAsync()).ShouldBe(2);
    });

    [Test]
    public Task Stop_And_Final_Result_Commit_Only_A_Valid_Terminal_State() => Both(async f =>
    {
        var request = Request();
        var run = (await Flow(f.Db).CreateInitialRunAsync(request, true))!;
        var results = await Race(f, 2, async (db, i) =>
        {
            if (i == 0) return (await Flow(db).RequestStopAsync(request.JobId, "alice", "stop")).Accepted;
            return await Flow(db).CompleteRunAsync(request.JobId, run.RunId, false);
        });
        results.Any(x => x).ShouldBeTrue();
        var job = await f.Db.DownloadJobs.AsNoTracking().SingleAsync();
        var storedRun = await f.Db.DownloadJobRuns.AsNoTracking().SingleAsync();
        job.Status.ShouldBeOneOf(DownloadJobStatus.Stopping, DownloadJobStatus.Completed);
        storedRun.Status.ShouldBe(job.Status);
        (job.StopRequestedAt is null).ShouldBe(job.Status == DownloadJobStatus.Completed);
    });

    [Test]
    public Task Concurrent_Content_Reservations_Deduplicate_Bytes_And_Allocate_Distinct_Versions() => Both(async f =>
    {
        var request = new VersionReservationRequest { JobId = Guid.NewGuid(), ContentHashXxh128 = new string('a', 32), StorageKey = "default", FileName = "file.mp4", Provider = "test", SourceMediaId = "source", LinkSourceToDownloadJob = false };
        var reservations = await Race(f, 4, (db, _) => new DownloadJobsRepository(db, new FixedClock(Now)).ReserveVersionAsync(request));
        reservations.Select(x => x.MediaGuid).Distinct().Count().ShouldBe(1);
        reservations.Count(x => !x.ContentAlreadyStored).ShouldBe(1);
        var later = await Race(f, 2, (db, i) => new DownloadJobsRepository(db, new FixedClock(Now))
            .ReserveVersionAsync(request with { ContentHashXxh128 = new string((char)('b' + i), 32) }));
        later.Select(x => x.VersionNum).Order().ShouldBe([2, 3]);
        (await f.Db.MediaContentIdVersions.AsNoTracking().CountAsync()).ShouldBe(3);
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(1);
    });

    [Test]
    public Task Concurrent_Playlist_Adds_And_Reorders_Preserve_Unique_Contiguous_Positions() => Both(async f =>
    {
        var media = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        f.Db.Media.AddRange(media.Select(id => new MediaEntity { MediaGuid = id }));
        await f.Db.SaveChangesAsync();
        var playlist = await new UserPlaylistsRepository(f.Db, new FixedClock(Now)).CreateAsync("alice", "concurrent", null);
        var id = playlist.Playlist.PlaylistId;
        var added = await Race(f, media.Length, (db, i) => new UserPlaylistsRepository(db, new FixedClock(Now)).AddItemAsync("alice", id, media[i], 1));
        added.All(x => x.Success).ShouldBeTrue();
        await Race(f, 2, (db, i) => new UserPlaylistsRepository(db, new FixedClock(Now)).ReorderItemsAsync("alice", id, i == 0 ? media : media.Reverse().ToArray()));
        var detail = (await new UserPlaylistsRepository(f.Db, new FixedClock(Now)).GetAsync("alice", id))!;
        detail.Items.Select(x => x.Position).ShouldBe(Enumerable.Range(1, 8));
        detail.Items.Select(x => x.MediaGuid).ToHashSet().SetEquals(media).ShouldBeTrue();
        var duplicate = await Race(f, 2, (db, _) => new UserPlaylistsRepository(db, new FixedClock(Now)).AddItemAsync("alice", id, media[0], null));
        duplicate.All(x => x.ErrorCode == "duplicate").ShouldBeTrue();
    });

    [Test]
    public Task Import_Batches_Are_Atomic_Deduplicated_And_Bound_Selected_Id_Parameters() => Both(async f =>
    {
        var repo = new ImportSessionRepository(f.Db, new FixedClock(Now));
        var session = await repo.CreateAsync(new() { StorageKey = "default" }, Guid.NewGuid(), Guid.NewGuid());
        var items = Enumerable.Range(0, 1201).Select(i => new ImportSessionScannedItem { RelativePath = $"media/{i}.mp4", FileName = $"{i}.mp4", SourceUrl = $"https://test/{i}", SidecarsJson = "{}" }).ToArray();
        await Should.ThrowAsync<DbUpdateException>(() => repo.IngestScannedItemsAsync(session.SessionId,
            [.. items, new() { RelativePath = "bad", FileName = "bad", SidecarsJson = "{" }]));
        (await f.Db.ImportSessionItems.AsNoTracking().CountAsync()).ShouldBe(0);
        await Race(f, 2, (db, _) => new ImportSessionRepository(db, new FixedClock(Now)).IngestScannedItemsAsync(session.SessionId, items));
        (await f.Db.ImportSessionItems.AsNoTracking().CountAsync()).ShouldBe(items.Length);
        var ids = await f.Db.ImportSessionItems.AsNoTracking().Select(x => x.ItemId).ToArrayAsync();
        if (f.Db.Database.IsSqlite())
        {
            await f.Db.Database.OpenConnectionAsync();
            SQLitePCL.raw.sqlite3_limit(((SqliteConnection)f.Db.Database.GetDbConnection()).Handle, 9, 900);
        }
        var result = await repo.ApplyBulkAsync(new() { SessionId = session.SessionId, Action = ImportSessionBulkAction.Include, ItemIds = ids });
        result.AffectedCount.ShouldBe(items.Length);
        (await f.Db.ImportSessions.AsNoTracking().SingleAsync()).ExcludedItems.ShouldBe(0);
        var selected = ids.Take(2).ToArray();
        (await repo.ListItemsForMetadataRefreshAsync(session.SessionId, selected, 10)).Select(x => x.ItemId).ToHashSet().SetEquals(selected).ShouldBeTrue();
        (await repo.ListItemsForEnrichAsync(session.SessionId, selected, 10)).Select(x => x.ItemId).ToHashSet().SetEquals(selected).ShouldBeTrue();
    });

    [Test]
    public Task Discovery_Batch_Upserts_Are_Atomic_And_Concurrent_Duplicates_Are_Shared() => Both(async f =>
    {
        var source = new CreatorSourceEntity { Platform = "test", SourceUrl = "https://test/channel" };
        var other = new CreatorSourceEntity { Platform = "test", SourceUrl = "https://test/other" };
        f.Db.CreatorSources.AddRange(source, other);
        await f.Db.SaveChangesAsync();
        var request = new UpsertDiscoveredMediaBatchRequestMessage
        {
            CreatorSourceId = source.Id, ScannedAt = Now, ScheduleKey = "test", IdempotencyKey = "test", ScanMode = CreatorSourceScanMode.Full, SuppressDownloadEnqueue = true,
            Items = Enumerable.Range(0, 500).Select(i => new DiscoveredMediaCandidate { Platform = "test", Extractor = "test", ExternalMediaId = i.ToString(), CanonicalUrl = "https://test/" + i }).ToArray()
        };
        var results = await Race(f, 2, (db, i) => new CreatorDiscoveryRepository(db, new FixedClock(Now)).UpsertDiscoveredMediaBatchAsync(request with { CreatorSourceId = i == 0 ? source.Id : other.Id }));
        results.Sum(x => x.NewCount).ShouldBe(500);
        (await f.Db.DiscoveredMedia.AsNoTracking().CountAsync()).ShouldBe(500);
        await Should.ThrowAsync<ArgumentException>(() => new CreatorDiscoveryRepository(f.Db, new FixedClock(Now)).UpsertDiscoveredMediaBatchAsync(request with
        {
            Items = [new() { Platform = "test", Extractor = "test", ExternalMediaId = "new", CanonicalUrl = "https://test/new" }, new() { Platform = "test", Extractor = "test", ExternalMediaId = "bad", CanonicalUrl = " " }]
        }));
        (await f.Db.DiscoveredMedia.AsNoTracking().CountAsync()).ShouldBe(500);
    });

    [Test]
    public Task Concurrent_Metadata_Replacement_Is_A_Complete_Graph_And_Late_Failure_Rolls_Back() => Both(async f =>
    {
        var media = await f.SeedMetadata();
        var first = Capture() with { Tags = ["first", "first-tail"] };
        var second = Capture() with { Tags = ["second", "second-tail"] };
        await Race(f, 2, async (db, i) => { await new MetadataRepository(db).WriteMetadataAsync(media, i == 0 ? first : second, "default"); return true; });
        var tags = (await new MetadataReadService(f.Database).GetDetailAsync(media))!.Tags;
        (tags.SequenceEqual(first.Tags) || tags.SequenceEqual(second.Tags)).ShouldBeTrue();
        await Should.ThrowAsync<Exception>(() => new MetadataRepository(f.Db).WriteMetadataAsync(media, Capture() with { Captions = [new() { StoragePath = "bad.vtt", LanguageCode = "en", CaptionType = "invalid" }] }, "default"));
        (await new MetadataReadService(f.Database).GetDetailAsync(media))!.Tags.ShouldBe(tags);
    });

    [Test]
    public Task Whole_Operation_Retries_Roll_Back_And_Only_Committed_Callbacks_Run() => Both(async f =>
    {
        var attempts = 0;
        var callbacks = 0;
        var id = Guid.NewGuid();
        await f.Db.MutateAsync("test.retry", async () =>
        {
            f.Db.Media.Add(new() { MediaGuid = id });
            await f.Db.SaveChangesAsync();
            await f.Db.AfterCommitAsync(() => { callbacks++; return Task.CompletedTask; });
            if (++attempts == 1)
                throw f.Db.Database.IsSqlite() ? new SqliteException("busy", 5) : new PostgresException("serialization", "ERROR", "ERROR", "40001");
        });
        attempts.ShouldBe(2);
        callbacks.ShouldBe(1);
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(1);
        attempts = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => f.Db.MutateAsync("test.failure", async () =>
        {
            attempts++;
            f.Db.Media.Add(new() { MediaGuid = Guid.NewGuid() });
            await f.Db.SaveChangesAsync();
            await f.Db.AfterCommitAsync(() => { callbacks++; return Task.CompletedTask; });
            throw new InvalidOperationException("not contention");
        }));
        attempts.ShouldBe(1);
        callbacks.ShouldBe(1);
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(1);
        // A callback failure occurs after commit and must not replay the database action.
        await Should.ThrowAsync<SqliteException>(() => f.Db.MutateAsync("test.callback", async () =>
        {
            attempts++;
            f.Db.Media.Add(new() { MediaGuid = Guid.NewGuid() });
            await f.Db.SaveChangesAsync();
            await f.Db.AfterCommitAsync(() => throw new SqliteException("callback", 5));
        }));
        attempts.ShouldBe(2);
        (await f.Db.Media.AsNoTracking().CountAsync()).ShouldBe(2);
    });

    [Test]
    public async Task Sqlite_Held_Writer_Has_Bounded_Cancellation_And_Timeout_Metrics()
    {
        await using var f = await Fixture.Create(false);
        await f.Db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)f.Db.Database.GetDbConnection();
        connection.DefaultTimeout = 1;
        using var scope = f.ScopeFactory.CreateScope();
        var blocker = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await using var held = await blocker.Database.BeginTransactionAsync();
        var timeoutCount = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) => { if (instrument.Name == "persistence.mutation.timeouts") current.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref timeoutCount, value));
        listener.Start();
        var elapsed = Stopwatch.StartNew();
        await Should.ThrowAsync<SqliteException>(() => Task.Run(() => f.Db.MutateAsync("test.timeout", () => Task.CompletedTask)));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        timeoutCount.ShouldBeGreaterThan(0);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        elapsed.Restart();
        await Should.ThrowAsync<OperationCanceledException>(() => Task.Run(() => f.Db.MutateAsync("test.cancel", () => Task.CompletedTask, cancelled.Token)));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        await held.RollbackAsync();
        await f.Db.MutateAsync("test.after-lock", async () => { f.Db.Media.Add(new() { MediaGuid = Guid.NewGuid() }); await f.Db.SaveChangesAsync(); });
        (await f.Db.Media.CountAsync()).ShouldBe(1);
    }

    [Test]
    public Task Retention_Rechecks_Restarted_Jobs_Active_Leases_And_Drained_Groups() => Both(async f =>
    {
        var request = Request();
        await Flow(f.Db).CreateInitialRunAsync(request, false);
        var old = Now - Duration.FromDays(40);
        await f.Db.DownloadJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedAt, old));
        await f.Db.DownloadGroups.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, DownloadGroupStatus.Completed).SetProperty(x => x.UpdatedAt, old).SetProperty(x => x.CompletedAt, old));
        var store = new ApplicationRetention(f.Db);
        var cutoff = Now - Duration.FromDays(30);
        DownloadJobStatus[] jobs = [DownloadJobStatus.Stopped];
        DownloadGroupStatus[] groups = [DownloadGroupStatus.Completed];
        DownloadJobStatus[] blocking = [DownloadJobStatus.Queued, DownloadJobStatus.Running, DownloadJobStatus.Stopping, DownloadJobStatus.Compensating];
        (await store.SelectJobsAsync(cutoff, jobs, groups, blocking, default)).Count.ShouldBe(1);
        (await Flow(f.Db).StartFreshRunAsync(request.JobId)).ShouldNotBeNull();
        (await store.DeleteJobsAsync([request.JobId], cutoff, jobs, groups, blocking, default)).ShouldBe(0);
        (await store.DeleteGroupsAsync([request.CorrelationId], default)).ShouldBe(0);
        (await f.Db.DownloadJobs.CountAsync()).ShouldBe(1);
        await f.Db.DownloadJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, DownloadJobStatus.Stopped).SetProperty(x => x.UpdatedAt, old));
        await f.Db.DownloadGroups.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, DownloadGroupStatus.Completed).SetProperty(x => x.UpdatedAt, old).SetProperty(x => x.CompletedAt, old));
        var runId = (await f.Db.DownloadJobRuns.AsNoTracking().SingleAsync()).RunId;
        f.Db.ChangeTracker.Clear();
        f.Db.DownloadWorkerLeases.Add(new() { DispatchId = Guid.NewGuid(), JobId = request.JobId, RunId = runId,
            WorkerInstanceId = "worker", Status = DownloadWorkerLeaseStatus.Active, ArtifactKey = "", AcquiredAt = old, LastHeartbeatAt = old, ExpiresAt = Now });
        await f.Db.SaveChangesAsync();
        (await store.DeleteJobsAsync([request.JobId], cutoff, jobs, groups, blocking, default)).ShouldBe(0);
        await f.Db.DownloadWorkerLeases.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, DownloadWorkerLeaseStatus.Expired));
        (await store.DeleteJobsAsync([request.JobId], cutoff, jobs, groups, blocking, default)).ShouldBe(1);
        (await store.DeleteGroupsAsync([request.CorrelationId], default)).ShouldBe(1);
        (await f.Db.DownloadJobRuns.CountAsync()).ShouldBe(0);

        var competing = Request();
        await Flow(f.Db).CreateInitialRunAsync(competing, false);
        await f.Db.DownloadJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedAt, old));
        await f.Db.DownloadGroups.ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedAt, old).SetProperty(x => x.CompletedAt, old));
        var winners = await Race(f, 2, async (db, i) => i == 0
            ? await Flow(db).StartFreshRunAsync(competing.JobId) is not null
            : await new ApplicationRetention(db).DeleteJobsAsync([competing.JobId], cutoff, jobs, groups, blocking, default) == 1);
        winners.Count(x => x).ShouldBe(1);
        var survivor = await f.Db.DownloadJobs.AsNoTracking().SingleOrDefaultAsync();
        if (survivor is not null)
        {
            survivor.Status.ShouldBe(DownloadJobStatus.Running);
            (await f.Db.DownloadJobRuns.CountAsync()).ShouldBe(1);
        }
        else
        {
            (await f.Db.DownloadJobRuns.CountAsync()).ShouldBe(0);
            (await f.Db.DownloadJobHistory.CountAsync()).ShouldBe(0);
        }
    });
    [Test]
    public Task Media_Deletion_Does_Not_Replay_Worker_Side_Effects_Or_Delete_New_Objects() => Both(async f =>
    {
        var media = Guid.NewGuid();
        f.Db.Media.Add(new() { MediaGuid = media });
        f.Db.MediaContentIdVersions.Add(new() { MediaGuid = media, VersionNum = 1, StorageKey = "default", StoragePath = "first.mp4", ContentHashXxh128 = new string('a', 32) });
        await f.Db.SaveChangesAsync();
        var bus = Substitute.For<IMessageBus>();
        var requests = 0;
        bus.RequestAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(MediaFileSubjects.Delete, Arg.Any<DeleteMediaFileRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref requests);
                // A writer adds an object while the Worker request is in flight.
                using var scope = f.ScopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
                db.MediaContentIdVersions.Add(new() { MediaGuid = media, VersionNum = 2, StorageKey = "default", StoragePath = "new.mp4", ContentHashXxh128 = new string('b', 32) });
                await db.SaveChangesAsync();
                return (DeleteMediaFileResponse?)new DeleteMediaFileResponse { Success = true };
            });
        var search = Substitute.For<ITypesenseIndexService>();
        var executor = new MediaDeleteExecutor(f.Database, bus, search, f.ScopeFactory, NullLogger<MediaDeleteExecutor>.Instance);
        var response = await executor.DeleteMediaAsync(media);
        response.ErrorCode.ShouldBe("conflict");
        requests.ShouldBe(1);
        (await f.Db.Media.CountAsync()).ShouldBe(1);
        (await f.Db.MediaContentIdVersions.CountAsync()).ShouldBe(2);
        search.ReceivedCalls().ShouldBeEmpty();
    });

    [Test]
    public Task Stale_Media_Cleanup_Is_Bounded_And_Protects_Active_Downloads() => Both(async f =>
    {
        var request = Request();
        await Flow(f.Db).CreateInitialRunAsync(request, true);
        var protectedMedia = Guid.NewGuid();
        f.Db.Media.AddRange(Enumerable.Range(0, 501).Select(_ => new MediaEntity { MediaGuid = Guid.NewGuid() }));
        f.Db.Media.Add(new() { MediaGuid = protectedMedia });
        f.Db.MediaSourceVersions.Add(new() { MediaGuid = protectedMedia, Provider = "test", SourceMediaId = "protected", LatestJobId = request.JobId });
        await f.Db.SaveChangesAsync();
        var store = new ApplicationRetention(f.Db);
        (await store.DeleteStaleMediaBatchAsync(default)).ShouldBe(ApplicationBatches.MembershipBatchSize);
        (await store.DeleteStaleMediaBatchAsync(default)).ShouldBe(101);
        (await store.DeleteStaleMediaBatchAsync(default)).ShouldBe(0);
        (await f.Db.Media.AsNoTracking().SingleAsync()).MediaGuid.ShouldBe(protectedMedia);
    });

    [Test]
    public Task Finalize_And_Stop_Race_Keeps_Attempt_Source_And_Playlist_Commit_Atomic() => Both(async f =>
    {
        var request = Request();
        var run = (await Flow(f.Db).CreateInitialRunAsync(request, true))!;
        var media = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        f.Db.Media.Add(new() { MediaGuid = media });
        f.Db.Playlists.Add(new() { PlaylistId = playlist, CorrelationId = request.CorrelationId, SourceUrl = "https://test/playlist" });
        f.Db.PlaylistItems.Add(new() { PlaylistId = playlist, PlaylistIndex = 1, JobId = request.JobId, EntryUrl = request.SourceUrl });
        await f.Db.SaveChangesAsync();
        var execution = new DownloadExecutionIdentity { JobId = request.JobId, CorrelationId = request.CorrelationId,
            RunId = run.RunId, DispatchId = Guid.NewGuid(), Stage = DownloadStage.Finalize, Attempt = 1 };
        (await Flow(f.Db).BeginStageAttemptAsync(execution, "finalize")).ShouldBeTrue();
        await Race(f, 2, async (db, i) => i == 0
            ? await Flow(db).FinalizeRunAsync(execution, media, "test", "source", Now)
            : (await Flow(db).RequestStopAsync(request.JobId, "alice", null)).Accepted);
        var job = await f.Db.DownloadJobs.AsNoTracking().SingleAsync();
        var attempt = await f.Db.DownloadStageAttempts.AsNoTracking().SingleAsync();
        var completed = job.Status == DownloadJobStatus.Completed;
        job.Status.ShouldBeOneOf(DownloadJobStatus.Completed, DownloadJobStatus.Stopping);
        (await f.Db.MediaPlaylistMemberships.CountAsync()).ShouldBe(completed ? 1 : 0);
        (await f.Db.MediaSourceVersions.CountAsync()).ShouldBe(completed ? 1 : 0);
        attempt.Status.ShouldBe(completed ? DownloadStageStatus.Succeeded : DownloadStageStatus.Pending);
        (await f.Db.DownloadJobRuns.AsNoTracking().SingleAsync()).Status.ShouldBe(job.Status);
    });

    [Test]
    public Task Cancelling_A_Waiting_Mutation_Does_Not_Modify_Job_Or_Run() => Both(async f =>
    {
        var request = Request();
        await Flow(f.Db).CreateInitialRunAsync(request, true);
        using var scope = f.ScopeFactory.CreateScope();
        var blocker = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.DownloadJobs.Where(x => x.JobId == request.JobId).ExecuteUpdateAsync(set => set.SetProperty(x => x.WarningCount, 10));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(() => Task.Run(async () =>
        {
            using var actor = f.ScopeFactory.CreateScope();
            await Flow(actor.ServiceProvider.GetRequiredService<DataBridgeDbContext>()).RequestStopAsync(request.JobId, "alice", null, cancelled.Token);
        }));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        await transaction.RollbackAsync();
        var job = await f.Db.DownloadJobs.AsNoTracking().SingleAsync();
        job.StopRequestedAt.ShouldBeNull();
        job.WarningCount.ShouldBe(0);
        job.Status.ShouldBe(DownloadJobStatus.Running);
        (await f.Db.DownloadJobRuns.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadJobStatus.Running);
    });

    [Test]
    public Task Import_Retention_Batches_Respect_Terminal_Status_Age_And_Cascade() => Both(async f =>
    {
        var old = Now - Duration.FromDays(40);
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        f.Db.ImportSessions.AddRange(ids.Select(id => new ImportSessionEntity { SessionId = id, CorrelationId = Guid.NewGuid(), SourceRoot = "incoming",
            SourceKind = ImportSessionSourceKind.WorkerIncoming, StorageKey = "default", Status = ImportSessionStatus.Completed, CompletedAt = old, UpdatedAt = old }));
        f.Db.ImportSessionItems.Add(new() { ItemId = Guid.NewGuid(), SessionId = ids[0], RelativePath = "file.mp4", FileName = "file.mp4" });
        await f.Db.SaveChangesAsync();
        var store = new ApplicationRetention(f.Db);
        (await store.SelectSessionsAsync(Now - Duration.FromDays(30), default)).Count.ShouldBe(500);
        // Selection is stale by the time deletion runs. Recheck the terminal predicate.
        await f.Db.ImportSessions.Where(x => x.SessionId == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, ImportSessionStatus.Committing));
        (await store.DeleteSessionsAsync(ids, Now - Duration.FromDays(30), default)).ShouldBe(500);
        (await f.Db.ImportSessionItems.CountAsync()).ShouldBe(1);
        (await f.Db.ImportSessions.AsNoTracking().SingleAsync()).SessionId.ShouldBe(ids[0]);
    });

    [Test]
    public Task Concurrent_Creator_Source_Creation_Reuses_The_Canonical_Identity() => Both(async f =>
    {
        var sources = await Race(f, 3, (db, _) => new CreatorDiscoveryRepository(db, new FixedClock(Now))
            .CreateOrReuseSourceAsync(new() { Platform = "youtube", SourceUrl = "https://www.youtube.com/@same/videos" }));
        sources.Select(x => x.Source.Id).Distinct().Count().ShouldBe(1);
        (await f.Db.CreatorSources.AsNoTracking().CountAsync()).ShouldBe(1);
        (await f.Db.CreatorScanStates.AsNoTracking().CountAsync()).ShouldBe(1);
        // A plain Create does not promise reuse. Its unique conflict is not a retryable upsert race.
        await Should.ThrowAsync<DbUpdateException>(() => new CreatorDiscoveryRepository(f.Db, new FixedClock(Now))
            .CreateSourceAsync(new() { Platform = "youtube", SourceUrl = "https://www.youtube.com/@same/videos" }));
        (await f.Db.CreatorSources.AsNoTracking().CountAsync()).ShouldBe(1);
    });

    [Test]
    public Task Heartbeat_And_Expiry_Race_Is_Atomic_And_Old_Leases_Cannot_Fail_A_New_Run() => Both(async f =>
    {
        var request = Request();
        var run = (await Flow(f.Db).CreateInitialRunAsync(request, true))!;
        var execution = new DownloadExecutionIdentity { JobId = request.JobId, CorrelationId = request.CorrelationId,
            RunId = run.RunId, DispatchId = Guid.NewGuid(), Stage = DownloadStage.Metadata, Attempt = 1 };
        await Flow(f.Db).BeginStageAttemptAsync(execution, "metadata");
        (await Flow(f.Db).TryAcquireLeaseAsync(new() { Execution = execution, WorkerInstanceId = "worker", OccurredAt = Now })).Granted.ShouldBeTrue();
        await Flow(f.Db).UpsertArtifactAsync(new() { JobId = request.JobId, RunId = run.RunId, Stage = DownloadStage.Metadata,
            ArtifactKey = "primary", Kind = UploadArtifactKind.Primary, Required = true, Status = DownloadArtifactStatus.Uploading,
            StorageKey = "default", StoragePath = "upload.mp4" });
        var decisions = await Race(f, 2, async (db, i) => i == 0
            ? (await new DownloadFlowV2Repository(db, new FixedClock(Now + Duration.FromSeconds(44)), NullDownloadJobStateNotifier.Instance)
                .TryRenewLeaseAsync(new() { DispatchId = execution.DispatchId, RunId = run.RunId, WorkerInstanceId = "worker", OccurredAt = Now })).Renewed
            : (await new DownloadFlowV2Repository(db, new FixedClock(Now + Duration.FromSeconds(46)), NullDownloadJobStateNotifier.Instance)
                .FailExpiredLeasesAsync()).Count == 1);
        decisions.Count(x => x).ShouldBe(1);
        var expired = (await f.Db.DownloadWorkerLeases.AsNoTracking().SingleAsync()).Status == DownloadWorkerLeaseStatus.Expired;
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).Status.ShouldBe(expired ? DownloadJobStatus.Failed : DownloadJobStatus.Running);
        (await f.Db.DownloadArtifacts.AsNoTracking().SingleAsync()).Status.ShouldBe(expired ? DownloadArtifactStatus.Residual : DownloadArtifactStatus.Uploading);
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).WarningCount.ShouldBe(expired ? 1 : 0);
        if (!expired)
        {
            await Flow(f.Db).RequestStopAsync(request.JobId, "alice", null);
            await Flow(f.Db).MarkStoppedAsync(request.JobId, run.RunId, null);
        }
        var fresh = (await Flow(f.Db).StartFreshRunAsync(request.JobId))!;
        await new DownloadFlowV2Repository(f.Db, new FixedClock(Now + Duration.FromSeconds(100)), NullDownloadJobStateNotifier.Instance).FailExpiredLeasesAsync();
        var job = await f.Db.DownloadJobs.AsNoTracking().SingleAsync();
        job.CurrentRunId.ShouldBe(fresh.RunId);
        job.Status.ShouldBe(DownloadJobStatus.Running);
        job.WarningCount.ShouldBe(0);
    });

}
