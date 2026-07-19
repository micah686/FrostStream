using DataBridge.Data;
using DataBridge.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;
using TUnit.Core;

namespace IntegrationTests.DataBridge;

public sealed class DownloadJobsRepositoryTests
{
    private static readonly PostgresFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static DownloadJobsRepositoryTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task ResetAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        await Fixture.ResetAsync();
    }

    [After(Test)]
    public void Release()
    {
        Gate.Release();
    }

    [Test]
    public async Task ReserveVersionAsync_Rolls_Back_Media_Insert_When_SaveChanges_Fails()
    {
        await using var db = Fixture.CreateDb(new FailingSaveChangesInterceptor());
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => repository.ReserveVersionAsync(new VersionReservationRequest
        {
            JobId = Guid.NewGuid(),
            ContentHashXxh128 = Guid.NewGuid().ToString("N"),
            StorageKey = "default",
            FileName = "video.mp4",
            Provider = "youtube",
            SourceMediaId = "source-a",
            SourceLastModified = Fixture.Now
        }));

        (await Fixture.CountAsync("media")).ShouldBe(0);
        (await Fixture.CountAsync("media_content_id_versions")).ShouldBe(0);
        (await Fixture.CountAsync("media_source_versions")).ShouldBe(0);
    }

    [Test]
    public async Task DeleteNewMediaGuidAsync_Does_Not_Delete_Media_When_SaveChanges_Fails()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaWithSourceAsync(mediaGuid, "youtube", "source-a");

        await using var db = Fixture.CreateDb(new FailingSaveChangesInterceptor());
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            repository.DeleteNewMediaGuidAsync(mediaGuid, "youtube", "source-a"));

        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        (await Fixture.CountAsync("media_source_versions", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
    }

    [Test]
    public async Task DeleteNewMediaGuidAsync_Removes_Media_And_Source_Row()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaWithSourceAsync(mediaGuid, "youtube", "source-a");

        await using var db = Fixture.CreateDb();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        await repository.DeleteNewMediaGuidAsync(mediaGuid, "youtube", "source-a");

        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(0);
        (await Fixture.CountAsync("media_source_versions", "media_guid = @media_guid", mediaGuid)).ShouldBe(0);
    }

    [Test]
    public async Task TryMarkMessageProcessedAsync_Returns_False_For_Duplicate_Message()
    {
        var messageId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        (await repository.TryMarkMessageProcessedAsync(messageId, "op-a", Guid.NewGuid())).ShouldBeTrue();
        (await repository.TryMarkMessageProcessedAsync(messageId, "op-a", Guid.NewGuid())).ShouldBeFalse();

        (await Fixture.CountAsync("processed_messages")).ShouldBe(1);
    }

    [Test]
    public async Task TryMarkMessageProcessedAsync_Rolls_Back_With_Transaction()
    {
        var messageId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            (await repository.TryMarkMessageProcessedAsync(messageId, "op-a", Guid.NewGuid())).ShouldBeTrue();
            await tx.RollbackAsync();
        }

        (await Fixture.CountAsync("processed_messages")).ShouldBe(0);
    }

    [Test]
    public async Task RequestStopAsync_Marks_Active_V2_Run_Stopping()
    {
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var initial = await repository.CreateInitialRunAsync(Request(jobId, correlationId), autoStart: true);
        initial.ShouldNotBeNull();

        var decision = await repository.RequestStopAsync(jobId, "tester", "stop requested");

        decision.Accepted.ShouldBeTrue();
        decision.Status.ShouldBe(DownloadJobStatus.Stopping);
        decision.RunId.ShouldBe(initial.RunId);
        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.Status.ShouldBe(DownloadJobStatus.Stopping);
        job.State.ShouldBe(DownloadJobState.Cancelling);
        job.FailureKind.ShouldBe(FailureKind.Stopped);
        job.FailureCode.ShouldBe("user_stopped");
        job.StopRequestedBy.ShouldBe("tester");
    }

    [Test]
    public async Task MarkStoppedAsync_Writes_Terminal_Stopped_Run()
    {
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var initial = await repository.CreateInitialRunAsync(Request(jobId, correlationId), autoStart: true);
        initial.ShouldNotBeNull();
        await repository.RequestStopAsync(jobId, "tester", "stop requested");

        (await repository.MarkStoppedAsync(jobId, initial.RunId, "stopped cleanly")).ShouldBeTrue();

        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.Status.ShouldBe(DownloadJobStatus.Stopped);
        job.State.ShouldBe(DownloadJobState.Cancelled);
        job.CompletedAt.ShouldNotBeNull();
        var run = await db.DownloadJobRuns.SingleAsync(x => x.RunId == initial.RunId);
        run.Status.ShouldBe(DownloadJobStatus.Stopped);
        run.EndedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task StartFreshRunAsync_Creates_New_Run_And_Resets_Attempts()
    {
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var initial = await repository.CreateInitialRunAsync(Request(jobId, correlationId), autoStart: true);
        initial.ShouldNotBeNull();
        await repository.RequestStopAsync(jobId, "tester", "stop requested");
        await repository.MarkStoppedAsync(jobId, initial.RunId, "stopped cleanly");

        var restarted = await repository.StartFreshRunAsync(jobId);

        restarted.ShouldNotBeNull();
        restarted.RunId.ShouldNotBe(initial.RunId);
        restarted.RunNumber.ShouldBe(2);
        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.Status.ShouldBe(DownloadJobStatus.Running);
        job.State.ShouldBe(DownloadJobState.MetadataPending);
        job.CurrentRunId.ShouldBe(restarted.RunId);
        job.CurrentRunNumber.ShouldBe(2);
        job.CurrentAttempt.ShouldBe(0);
        job.FailureKind.ShouldBeNull();
        job.FailureCode.ShouldBeNull();
        job.FailureMessage.ShouldBeNull();
        job.CompletedAt.ShouldBeNull();
        (await db.DownloadJobRuns.CountAsync(x => x.JobId == jobId)).ShouldBe(2);
    }

    [Test]
    public async Task QueryQueueAsync_Filters_By_State_SourceKind_RequestedBy_StorageKey_And_Query()
    {
        await using var db = Fixture.CreateDb();
        var completedId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        db.DownloadJobs.Add(Job(completedId, DownloadJobState.Completed, sourceUrl: "https://example.test/cats", requestedBy: "alice", storageKey: "nas", sourceKind: DownloadSourceKind.Playlist));
        db.DownloadJobs.Add(Job(Guid.NewGuid(), DownloadJobState.Completed, sourceUrl: "https://example.test/dogs", requestedBy: "bob", storageKey: "s3", sourceKind: DownloadSourceKind.Direct));
        db.DownloadJobs.Add(Job(Guid.NewGuid(), DownloadJobState.Cancelled, sourceUrl: "https://example.test/cats-2", requestedBy: "alice", storageKey: "nas", sourceKind: DownloadSourceKind.Playlist));
        db.DownloadJobs.Add(Job(Guid.NewGuid(), DownloadJobState.DownloadPending, sourceUrl: "https://example.test/active"));
        db.DownloadJobs.Add(Job(Guid.NewGuid(), DownloadJobState.DownloadQueued, sourceUrl: "https://example.test/queued"));
        db.DownloadJobs.Add(Job(failedId, DownloadJobState.ProviderHalted, sourceUrl: "https://example.test/halted"));
        await db.SaveChangesAsync();
        await db.DownloadJobs
            .Where(x => x.JobId == failedId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FailureCode, "bot_check")
                .SetProperty(x => x.FailureMessage, "Provider requested verification"));
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        (await repository.QueryQueueAsync(new DownloadQueueListRequest { State = DownloadJobState.Completed }))
            .TotalCount.ShouldBe(2);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { SourceKind = DownloadSourceKind.Playlist }))
            .TotalCount.ShouldBe(2);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { RequestedBy = "bob" }))
            .TotalCount.ShouldBe(1);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StorageKey = "nas" }))
            .TotalCount.ShouldBe(2);
        // Case-insensitive substring match against the source URL.
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { Query = "CATS" }))
            .TotalCount.ShouldBe(2);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { Query = completedId.ToString()[..8] }))
            .Items.ShouldHaveSingleItem().JobId.ShouldBe(completedId);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { Query = "bot_check" }))
            .Items.ShouldHaveSingleItem().JobId.ShouldBe(failedId);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StateGroup = DownloadQueueStateGroup.Active }))
            .Items.ShouldHaveSingleItem().State.ShouldBe(DownloadJobState.DownloadPending);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StateGroup = DownloadQueueStateGroup.Queued }))
            .Items.ShouldHaveSingleItem().State.ShouldBe(DownloadJobState.DownloadQueued);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StateGroup = DownloadQueueStateGroup.Failed }))
            .Items.ShouldHaveSingleItem().JobId.ShouldBe(failedId);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StateGroup = DownloadQueueStateGroup.Done }))
            .TotalCount.ShouldBe(2);
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StateGroup = DownloadQueueStateGroup.Stopped }))
            .Items.ShouldHaveSingleItem().State.ShouldBe(DownloadJobState.Cancelled);

        var combined = await repository.QueryQueueAsync(new DownloadQueueListRequest
        {
            State = DownloadJobState.Completed,
            RequestedBy = "alice",
            Query = "cats"
        });
        combined.Items.ShouldHaveSingleItem().SourceUrl.ShouldBe("https://example.test/cats");
    }

    [Test]
    public async Task QueryQueueAsync_Filters_By_Created_Time_Range()
    {
        await using var db = Fixture.CreateDb();
        var early = Instant.FromUtc(2026, 1, 1, 0, 0);
        var mid = Instant.FromUtc(2026, 3, 1, 0, 0);
        var late = Instant.FromUtc(2026, 6, 1, 0, 0);
        var earlyId = Guid.NewGuid();
        var midId = Guid.NewGuid();
        var lateId = Guid.NewGuid();
        db.DownloadJobs.Add(Job(earlyId, DownloadJobState.Completed));
        db.DownloadJobs.Add(Job(midId, DownloadJobState.Completed));
        db.DownloadJobs.Add(Job(lateId, DownloadJobState.Completed));
        await db.SaveChangesAsync();
        await SetCreatedAtAsync(db, earlyId, early);
        await SetCreatedAtAsync(db, midId, mid);
        await SetCreatedAtAsync(db, lateId, late);

        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        var page = await repository.QueryQueueAsync(new DownloadQueueListRequest { CreatedFrom = mid });
        page.TotalCount.ShouldBe(2);
        page.Items.Select(x => x.JobId).ShouldBe([lateId, midId]); // created_at desc

        var windowed = await repository.QueryQueueAsync(new DownloadQueueListRequest { CreatedFrom = mid, CreatedTo = mid });
        windowed.Items.ShouldHaveSingleItem().JobId.ShouldBe(midId);
    }

    [Test]
    public async Task QueryQueueAsync_Paginates_Deterministically()
    {
        await using var db = Fixture.CreateDb();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.DownloadJobs.Add(Job(id, DownloadJobState.Completed));
        }
        await db.SaveChangesAsync();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        var seen = new List<Guid>();
        string? cursor = null;
        var guard = 0;
        do
        {
            var page = await repository.QueryQueueAsync(new DownloadQueueListRequest { Limit = 2, Cursor = cursor });
            page.TotalCount.ShouldBe(5);
            seen.AddRange(page.Items.Select(x => x.JobId));
            cursor = page.NextCursor;
            (++guard).ShouldBeLessThan(10);
        }
        while (cursor is not null);

        seen.Count.ShouldBe(5);
        seen.ToHashSet().Count.ShouldBe(5); // no duplicates / gaps
        seen.ShouldBe(seen.ToHashSet().ToList()); // each exactly once
        seen.OrderBy(x => x).ShouldBe(ids.OrderBy(x => x));
    }

    [Test]
    public async Task GetQueueJobAsync_Returns_Snapshot_Or_Null()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        db.DownloadJobs.Add(Job(jobId, DownloadJobState.ProviderHalted, requestedBy: "alice"));
        await db.SaveChangesAsync();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        var job = await repository.GetQueueJobAsync(jobId);
        job.ShouldNotBeNull();
        job.State.ShouldBe(DownloadJobState.ProviderHalted);
        job.RequestedBy.ShouldBe("alice");

        (await repository.GetQueueJobAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Test]
    public async Task GetQueueHistoryAsync_Returns_Ordered_Entries_And_Null_For_Missing_Job()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        db.DownloadJobs.Add(Job(jobId, DownloadJobState.Completed));
        await db.SaveChangesAsync();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        await repository.RecordHistoryAsync(jobId, Guid.NewGuid(), "op/1", nameof(DownloadRequested), "{}");
        await repository.RecordHistoryAsync(jobId, Guid.NewGuid(), "op/2", nameof(MetadataFetched), "{}");
        await repository.RecordHistoryAsync(jobId, Guid.NewGuid(), "op/3", nameof(DownloadCompleted), null);

        var entries = await repository.GetQueueHistoryAsync(jobId);
        entries.ShouldNotBeNull();
        entries.Select(x => x.EventName).ShouldBe(
        [
            nameof(DownloadRequested),
            nameof(MetadataFetched),
            nameof(DownloadCompleted)
        ]);
        entries.Select(x => x.Id).ShouldBeInOrder();

        (await repository.GetQueueHistoryAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Test]
    public async Task QueryQueueAsync_On_Empty_Table_Returns_Empty_Page()
    {
        await using var db = Fixture.CreateDb();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        var page = await repository.QueryQueueAsync(new DownloadQueueListRequest());

        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
    }

    [Test]
    public async Task V2_Stage_Transition_Publishes_Status_And_Attempt()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var notifier = new CapturingStateNotifier();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, notifier);
        var run = await repository.CreateInitialRunAsync(Request(jobId, Guid.NewGuid()), autoStart: true);
        run.ShouldNotBeNull();
        var execution = new DownloadExecutionIdentity
        {
            JobId = jobId,
            RunId = run.RunId,
            CorrelationId = run.Request.CorrelationId,
            DispatchId = Guid.NewGuid(),
            Stage = DownloadStage.Metadata,
            Attempt = 1
        };

        (await repository.BeginStageAttemptAsync(execution, "metadata/attempt/1")).ShouldBeTrue();

        notifier.V2Calls.Count.ShouldBe(2);
        notifier.V2Calls[^1].JobId.ShouldBe(jobId);
        notifier.V2Calls[^1].Status.ShouldBe(DownloadJobStatus.Running);
        notifier.V2Calls[^1].Stage.ShouldBe(DownloadStage.Metadata);
        notifier.V2Calls[^1].Attempt.ShouldBe(1);
    }

    [Test]
    public async Task Stop_After_Dispatch_Allows_One_Drain_Lease_That_Is_Already_Cancelled()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(
            db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var run = await repository.CreateInitialRunAsync(Request(jobId, Guid.NewGuid()), autoStart: true);
        run.ShouldNotBeNull();
        var execution = Execution(run, DownloadStage.Metadata, 1);
        (await repository.BeginStageAttemptAsync(execution, "metadata/attempt/1")).ShouldBeTrue();
        (await repository.RequestStopAsync(jobId, "tester", "stop before claim"))
            .Status.ShouldBe(DownloadJobStatus.Stopping);

        var lease = await repository.TryAcquireLeaseAsync(new AcquireDownloadLeaseRequest
        {
            Execution = execution,
            WorkerInstanceId = "worker-drain",
            OccurredAt = SystemClock.Instance.GetCurrentInstant()
        });

        lease.Granted.ShouldBeTrue();
        lease.StopRequested.ShouldBeTrue();
        (await repository.CanAcceptWorkerEventAsync(execution)).ShouldBeTrue();
    }

    [Test]
    public async Task V2_Reservation_Defers_Source_Mapping_Until_Atomic_Finalize()
    {
        var jobId = Guid.NewGuid();
        const string provider = "youtube";
        const string sourceMediaId = "deferred-source";
        await using var db = Fixture.CreateDb();
        var v2 = new DownloadFlowV2Repository(
            db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var run = await v2.CreateInitialRunAsync(Request(jobId, Guid.NewGuid()), autoStart: true);
        run.ShouldNotBeNull();
        var jobs = new DownloadJobsRepository(db, SystemClock.Instance);
        var reservation = await jobs.ReserveVersionAsync(new VersionReservationRequest
        {
            JobId = jobId,
            ContentHashXxh128 = Guid.NewGuid().ToString("N"),
            StorageKey = "default",
            FileName = "video.mp4",
            Provider = provider,
            SourceMediaId = sourceMediaId,
            SourceLastModified = SystemClock.Instance.GetCurrentInstant(),
            LinkSourceToDownloadJob = false,
            PersistSourceMapping = false
        });
        (await db.MediaSourceVersions.CountAsync(x => x.SourceMediaId == sourceMediaId)).ShouldBe(0);
        var execution = Execution(run, DownloadStage.Finalize, 1);
        (await v2.BeginStageAttemptAsync(execution, "finalize/attempt/1")).ShouldBeTrue();

        (await v2.FinalizeRunAsync(
            execution,
            reservation.MediaGuid,
            provider,
            sourceMediaId,
            SystemClock.Instance.GetCurrentInstant())).ShouldBeTrue();

        var source = await db.MediaSourceVersions.SingleAsync(x => x.SourceMediaId == sourceMediaId);
        source.MediaGuid.ShouldBe(reservation.MediaGuid);
        source.LatestJobId.ShouldBe(jobId);
    }

    [Test]
    public async Task Expired_Lease_During_Stop_Fails_Run_Instead_Of_Leaving_It_Stopping()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(
            db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var run = await repository.CreateInitialRunAsync(Request(jobId, Guid.NewGuid()), autoStart: true);
        run.ShouldNotBeNull();
        var execution = Execution(run, DownloadStage.MediaAcquire, 1);
        (await repository.BeginStageAttemptAsync(execution, "media/attempt/1")).ShouldBeTrue();
        (await repository.TryAcquireLeaseAsync(new AcquireDownloadLeaseRequest
        {
            Execution = execution,
            WorkerInstanceId = "worker-lost",
            OccurredAt = SystemClock.Instance.GetCurrentInstant()
        })).Granted.ShouldBeTrue();
        await repository.RequestStopAsync(jobId, "tester", "stop while worker disappears");
        var expiredAt = SystemClock.Instance.GetCurrentInstant() - Duration.FromSeconds(1);
        await db.DownloadWorkerLeases.Where(x => x.DispatchId == execution.DispatchId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExpiresAt, expiredAt));

        (await repository.FailExpiredLeasesAsync()).Count.ShouldBe(1);

        var failed = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        failed.Status.ShouldBe(DownloadJobStatus.Failed);
        failed.FailureKind.ShouldBe(FailureKind.Interrupted);
        failed.FailureCode.ShouldBe("worker_lease_expired");
    }

    [Test]
    public async Task FinalizeRunAsync_Commits_Playlist_Link_And_Terminal_Status_Together()
    {
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaWithSourceAsync(mediaGuid, "youtube", "finalize-media");
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var run = await repository.CreateInitialRunAsync(Request(jobId, correlationId), autoStart: true);
        run.ShouldNotBeNull();
        db.Playlists.Add(new PlaylistEntity
        {
            PlaylistId = playlistId,
            CorrelationId = correlationId,
            State = PlaylistState.MetadataResolved,
            SourceUrl = $"https://example.test/playlist/{playlistId:N}"
        });
        db.PlaylistItems.Add(new PlaylistItemEntity
        {
            PlaylistId = playlistId,
            JobId = jobId,
            PlaylistIndex = 4,
            EntryUrl = run.Request.SourceUrl
        });
        await db.SaveChangesAsync();
        var execution = Execution(run, DownloadStage.Finalize, 1);
        (await repository.BeginStageAttemptAsync(execution, "finalize/attempt/1")).ShouldBeTrue();

        (await repository.FinalizeRunAsync(execution, mediaGuid)).ShouldBeTrue();

        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.Status.ShouldBe(DownloadJobStatus.Completed);
        job.Stage.ShouldBe(DownloadStage.Finalize);
        job.StageStatus.ShouldBe(DownloadStageStatus.Succeeded);
        (await db.MediaPlaylistMemberships.SingleAsync()).MediaGuid.ShouldBe(mediaGuid);
        (await db.DownloadStageAttempts.SingleAsync(x => x.DispatchId == execution.DispatchId))
            .Status.ShouldBe(DownloadStageStatus.Succeeded);
    }

    [Test]
    public async Task Stop_Wins_Against_Finalize_Without_Creating_Playlist_Membership()
    {
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaWithSourceAsync(mediaGuid, "youtube", "stopped-finalize-media");
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var run = await repository.CreateInitialRunAsync(Request(jobId, correlationId), autoStart: true);
        run.ShouldNotBeNull();
        db.Playlists.Add(new PlaylistEntity
        {
            PlaylistId = playlistId,
            CorrelationId = correlationId,
            State = PlaylistState.MetadataResolved,
            SourceUrl = $"https://example.test/playlist/{playlistId:N}"
        });
        db.PlaylistItems.Add(new PlaylistItemEntity
        {
            PlaylistId = playlistId,
            JobId = jobId,
            PlaylistIndex = 1,
            EntryUrl = run.Request.SourceUrl
        });
        await db.SaveChangesAsync();
        var execution = Execution(run, DownloadStage.Finalize, 1);
        (await repository.BeginStageAttemptAsync(execution, "finalize/attempt/1")).ShouldBeTrue();
        (await repository.RequestStopAsync(jobId, "tester", "race finalization")).Accepted.ShouldBeTrue();

        (await repository.FinalizeRunAsync(execution, mediaGuid)).ShouldBeFalse();

        (await db.MediaPlaylistMemberships.CountAsync()).ShouldBe(0);
        (await db.DownloadJobs.SingleAsync(x => x.JobId == jobId)).Status.ShouldBe(DownloadJobStatus.Stopping);
    }

    [Test]
    public async Task Old_Run_Event_Is_Rejected_After_User_Starts_A_Fresh_Run()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var first = await repository.CreateInitialRunAsync(Request(jobId, Guid.NewGuid()), autoStart: true);
        first.ShouldNotBeNull();
        var oldExecution = Execution(first, DownloadStage.Metadata, 1);
        (await repository.BeginStageAttemptAsync(oldExecution, "metadata/attempt/1")).ShouldBeTrue();
        (await repository.TryAcquireLeaseAsync(new AcquireDownloadLeaseRequest
        {
            Execution = oldExecution,
            WorkerInstanceId = "worker-a",
            OccurredAt = SystemClock.Instance.GetCurrentInstant()
        })).Granted.ShouldBeTrue();
        await repository.RequestStopAsync(jobId, "tester", "restart it");
        await repository.MarkStoppedAsync(jobId, first.RunId, "stopped");
        var second = await repository.StartFreshRunAsync(jobId);
        second.ShouldNotBeNull();

        (await repository.CanAcceptWorkerEventAsync(oldExecution)).ShouldBeFalse();
        second.RunId.ShouldNotBe(first.RunId);
    }

    [Test]
    public async Task Provider_Circuit_Requires_Clear_And_Explicit_Fresh_Start()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var request = Request(jobId, Guid.NewGuid()) with { SourceUrl = "https://www.youtube.com/watch?v=blocked" };
        (await repository.CreateInitialRunAsync(request, autoStart: false)).ShouldBeNull();
        await repository.OpenProviderCircuitAsync("youtube", "Provider requested verification.");

        (await repository.StartFreshRunAsync(jobId)).ShouldBeNull();
        var blocked = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        blocked.Status.ShouldBe(DownloadJobStatus.Stopped);
        blocked.FailureKind.ShouldBe(FailureKind.ProviderBlocked);
        blocked.FailureCode.ShouldBe("provider_circuit_open");

        await repository.ClearProviderCircuitAsync("youtube");
        var started = await repository.StartFreshRunAsync(jobId);
        started.ShouldNotBeNull();
        started.RunNumber.ShouldBe(1);
    }

    [Test]
    public async Task Startup_Reconciliation_Stops_Queued_And_Fails_Begun_Runs_Without_Creating_New_Runs()
    {
        var queuedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        await repository.CreateInitialRunAsync(Request(queuedId, Guid.NewGuid()), autoStart: false);
        await db.DownloadJobs.Where(x => x.JobId == queuedId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, DownloadJobStatus.Queued)
            .SetProperty(x => x.StageStatus, DownloadStageStatus.Pending));
        var active = await repository.CreateInitialRunAsync(Request(activeId, Guid.NewGuid()), autoStart: true);
        active.ShouldNotBeNull();
        var runsBefore = await db.DownloadJobRuns.CountAsync();

        var result = await repository.ReconcileForStartupAsync();

        result.StoppedQueuedJobs.ShouldBe(1);
        result.FailedActiveJobs.ShouldBe(1);
        (await db.DownloadJobs.SingleAsync(x => x.JobId == queuedId)).Status.ShouldBe(DownloadJobStatus.Stopped);
        var failed = await db.DownloadJobs.SingleAsync(x => x.JobId == activeId);
        failed.Status.ShouldBe(DownloadJobStatus.Failed);
        failed.FailureKind.ShouldBe(FailureKind.Interrupted);
        (await db.DownloadJobRuns.CountAsync()).ShouldBe(runsBefore);
    }

    [Test]
    public async Task Group_Stop_Remains_Stopping_Until_Active_Children_Settle_Then_Becomes_Stopped()
    {
        var correlationId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        var repository = new DownloadFlowV2Repository(db, SystemClock.Instance, NullDownloadJobStateNotifier.Instance);
        var completedRun = await repository.CreateInitialRunAsync(Request(completedId, correlationId), autoStart: true);
        var activeRun = await repository.CreateInitialRunAsync(Request(activeId, correlationId), autoStart: true);
        completedRun.ShouldNotBeNull();
        activeRun.ShouldNotBeNull();
        (await repository.CompleteRunAsync(completedId, completedRun.RunId, withWarnings: false)).ShouldBeTrue();

        await repository.StopGroupAsync(correlationId, "tester", "stop collection");

        (await db.DownloadGroups.SingleAsync(x => x.CorrelationId == correlationId))
            .Status.ShouldBe(DownloadGroupStatus.Stopping);
        (await repository.MarkStoppedAsync(activeId, activeRun.RunId, "stopped")).ShouldBeTrue();
        (await db.DownloadGroups.SingleAsync(x => x.CorrelationId == correlationId))
            .Status.ShouldBe(DownloadGroupStatus.Stopped);
    }

    private sealed class CapturingStateNotifier : IDownloadJobStateNotifier
    {
        public List<(Guid JobId, DownloadJobState NewState, DownloadJobState PreviousState)> Calls { get; } = [];
        public List<(Guid JobId, DownloadJobStatus Status, DownloadStage Stage, int Attempt)> V2Calls { get; } = [];

        public Task NotifyAsync(Guid jobId, DownloadJobState newState, DownloadJobState previousState, Guid correlationId, CancellationToken ct = default)
        {
            Calls.Add((jobId, newState, previousState));
            return Task.CompletedTask;
        }

        public Task NotifyV2Async(Guid jobId, Guid correlationId, DownloadJobStatus status,
            DownloadJobStatus previousStatus, DownloadStage stage, DownloadStageStatus stageStatus,
            Guid? runId, int runNumber, int attempt, string? artifactKey, int warningCount,
            CancellationToken ct = default)
        {
            V2Calls.Add((jobId, status, stage, attempt));
            return Task.CompletedTask;
        }
    }

    private static Task SetCreatedAtAsync(DataBridgeDbContext db, Guid jobId, Instant createdAt)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE download_jobs SET created_at = {createdAt} WHERE job_id = {jobId}");

    private static DownloadJobEntity Job(
        Guid jobId,
        DownloadJobState state,
        string sourceUrl = "https://example.test/video",
        string? requestedBy = null,
        string storageKey = "default",
        DownloadSourceKind sourceKind = DownloadSourceKind.Direct)
        => new()
        {
            JobId = jobId,
            CorrelationId = Guid.NewGuid(),
            State = state,
            Status = LegacyStatus(state),
            SourceUrl = sourceUrl,
            RequestedBy = requestedBy,
            StorageKey = storageKey,
            SourceKind = sourceKind,
            IngestOrigin = IngestOrigin.Download
        };

    private static DownloadRequested Request(Guid jobId, Guid correlationId)
        => new()
        {
            JobId = jobId,
            CorrelationId = correlationId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"job/{jobId:N}/requested",
            OccurredAt = SystemClock.Instance.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = "https://example.test/video",
            StorageKey = "default",
            SourceKind = DownloadSourceKind.Direct
        };

    private static DownloadExecutionIdentity Execution(
        DownloadRunRequest run,
        DownloadStage stage,
        int attempt,
        string? artifactKey = null)
        => new()
        {
            JobId = run.Request.JobId,
            RunId = run.RunId,
            CorrelationId = run.Request.CorrelationId,
            DispatchId = Guid.NewGuid(),
            Stage = stage,
            ArtifactKey = artifactKey,
            Attempt = attempt
        };

    private static DownloadJobStatus LegacyStatus(DownloadJobState state)
        => state switch
        {
            DownloadJobState.Queued or DownloadJobState.DownloadQueued => DownloadJobStatus.Queued,
            DownloadJobState.Completed => DownloadJobStatus.Completed,
            DownloadJobState.AlreadyDownloaded => DownloadJobStatus.AlreadyDownloaded,
            DownloadJobState.Ignored => DownloadJobStatus.Ignored,
            DownloadJobState.Cancelled => DownloadJobStatus.Stopped,
            DownloadJobState.FailedPermanent or DownloadJobState.FailedTransient
                or DownloadJobState.ProviderHalted => DownloadJobStatus.Failed,
            _ => DownloadJobStatus.Running
        };

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private readonly IContainer _postgresContainer = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_DB", "froststream_download_jobs_repository_tests")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithPortBinding(5432, true)
            .Build();

        private bool _initialized;

        public Instant Now { get; } = Instant.FromUtc(2026, 6, 1, 0, 0);

        private string ConnectionString =>
            new NpgsqlConnectionStringBuilder
            {
                Host = _postgresContainer.Hostname,
                Port = _postgresContainer.GetMappedPublicPort(5432),
                Database = "froststream_download_jobs_repository_tests",
                Username = "postgres",
                Password = "postgres",
                SearchPath = "storage,downloads,media,maintenance,metadata,auth,public"
            }.ConnectionString;

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            await _postgresContainer.StartAsync();
            await WaitForPostgresAsync();
            await RunMigrationsAsync();
            _initialized = true;
        }

        public async Task ResetAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "TRUNCATE TABLE media, download_jobs, processed_messages RESTART IDENTITY CASCADE;",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        public DataBridgeDbContext CreateDb(SaveChangesInterceptor? interceptor = null)
        {
            var optionsBuilder = new DbContextOptionsBuilder<DataBridgeDbContext>()
                .UseNpgsql(
                    ConnectionString,
                    npgsqlOptions => npgsqlOptions
                        .UseNodaTime()
                        .MapEnum<LocalStorageProtocol>("local_storage_protocol", "storage")
                        .MapEnum<NetworkStorageProtocol>("network_storage_protocol", "storage")
                        .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider", "storage")
                        .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode", "storage")
                        .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode", "storage")
                        .MapEnum<DownloadJobState>("download_job_state", "downloads")
                        .MapEnum<DownloadJobStatus>("download_job_status", "downloads")
                        .MapEnum<DownloadStage>("download_stage", "downloads")
                        .MapEnum<DownloadStageStatus>("download_stage_status", "downloads")
                        .MapEnum<DownloadGroupKind>("download_group_kind", "downloads")
                        .MapEnum<DownloadGroupStatus>("download_group_status", "downloads")
                        .MapEnum<DownloadArtifactStatus>("download_artifact_status", "downloads")
                        .MapEnum<DownloadWorkerLeaseStatus>("download_worker_lease_status", "downloads")
                        .MapEnum<FailureKind>("failure_kind", "downloads")
                        .MapEnum<IngestOrigin>("ingest_origin", "media")
                        .MapEnum<PlaylistState>("playlist_state", "playlists"))
                .UseSnakeCaseNamingConvention();

            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }

            return new DataBridgeDbContext(optionsBuilder.Options);
        }

        public async Task SeedMediaWithSourceAsync(Guid mediaGuid, string provider, string sourceMediaId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var mediaCommand = new NpgsqlCommand(
                             "INSERT INTO media (media_guid) VALUES (@media_guid);",
                             connection,
                             transaction))
            {
                mediaCommand.Parameters.AddWithValue("media_guid", mediaGuid);
                await mediaCommand.ExecuteNonQueryAsync();
            }

            await using (var sourceCommand = new NpgsqlCommand("""
                INSERT INTO media_source_versions
                    (provider, source_media_id, media_guid)
                VALUES
                    (@provider, @source_media_id, @media_guid);
                """, connection, transaction))
            {
                sourceCommand.Parameters.AddWithValue("provider", provider);
                sourceCommand.Parameters.AddWithValue("source_media_id", sourceMediaId);
                sourceCommand.Parameters.AddWithValue("media_guid", mediaGuid);
                await sourceCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async Task<long> CountAsync(string table)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*)::bigint FROM {table};", connection);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task<long> CountAsync(string table, string whereClause, Guid mediaGuid)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*)::bigint FROM {table} WHERE {whereClause};", connection);
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async ValueTask DisposeAsync()
        {
            await _postgresContainer.DisposeAsync();
        }

        private async Task RunMigrationsAsync()
        {
            var services = new ServiceCollection();
            services
                .AddFluentMigratorCore()
                .ConfigureRunner(runner => runner
                    .AddPostgres()
                    .WithGlobalConnectionString(ConnectionString)
                    .ScanIn(typeof(DownloadRequestedIngressService).Assembly).For.Migrations());

            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        private async Task WaitForPostgresAsync()
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync();
                    await connection.CloseAsync();
                    return;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            throw new TimeoutException("PostgreSQL container did not become reachable in time.");
        }
    }
}
