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
    public async Task TryBeginCancellationAsync_Marks_Queued_Job_Cancelling()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        db.DownloadJobs.Add(Job(jobId, DownloadJobState.DownloadQueued));
        await db.SaveChangesAsync();

        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        var decision = await repository.TryBeginCancellationAsync(jobId, "tester", "stop requested");

        decision.Accepted.ShouldBeTrue();
        decision.State.ShouldBe(DownloadJobState.Cancelling);
        decision.PreviousState.ShouldBe(DownloadJobState.DownloadQueued);
        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.State.ShouldBe(DownloadJobState.Cancelling);
        job.FailureKind.ShouldBe(FailureKind.Cancelled);
        job.FailureCode.ShouldBe("cancel_requested");
    }

    [Test]
    public async Task MarkCancelledAsync_Writes_Terminal_State_And_Failed_Row()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        db.DownloadJobs.Add(Job(jobId, DownloadJobState.Cancelling));
        await db.SaveChangesAsync();

        var repository = new DownloadJobsRepository(db, SystemClock.Instance);

        await repository.MarkCancelledAsync(jobId, "cancelled cleanly");

        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.State.ShouldBe(DownloadJobState.Cancelled);
        job.CompletedAt.ShouldNotBeNull();
        var failed = await db.FailedDownloadJobs.SingleAsync(x => x.JobId == jobId);
        failed.FailedState.ShouldBe(DownloadJobState.Cancelled);
        failed.FailureKind.ShouldBe(FailureKind.Cancelled);
        failed.FailureMessage.ShouldBe("cancelled cleanly");
    }

    [Test]
    public async Task UpdateStateAsync_Allows_Cancelled_Job_To_Requeue_For_Restart()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        db.DownloadJobs.Add(Job(jobId, DownloadJobState.Cancelling));
        await db.SaveChangesAsync();

        var notifier = new CapturingStateNotifier();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance, notifier);

        await repository.MarkCancelledAsync(jobId, "cancelled cleanly");
        await repository.UpdateStateAsync(jobId, DownloadJobState.Queued);

        var job = await db.DownloadJobs.SingleAsync(x => x.JobId == jobId);
        job.State.ShouldBe(DownloadJobState.Queued);
        job.FailureKind.ShouldBeNull();
        job.FailureCode.ShouldBeNull();
        job.FailureMessage.ShouldBeNull();
        job.CompletedAt.ShouldBeNull();
        notifier.Calls[^1].ShouldBe((jobId, DownloadJobState.Queued, DownloadJobState.Cancelled));
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
        (await repository.QueryQueueAsync(new DownloadQueueListRequest { StateGroup = DownloadQueueStateGroup.Cancelled }))
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
    public async Task State_Transitions_Publish_State_Changed_Only_On_Actual_Change()
    {
        var jobId = Guid.NewGuid();
        await using var db = Fixture.CreateDb();
        db.DownloadJobs.Add(Job(jobId, DownloadJobState.Queued));
        await db.SaveChangesAsync();

        var notifier = new CapturingStateNotifier();
        var repository = new DownloadJobsRepository(db, SystemClock.Instance, notifier);

        await repository.UpdateStateAsync(jobId, DownloadJobState.MetadataPending);
        await repository.UpdateStateAsync(jobId, DownloadJobState.MetadataPending); // no-op, unchanged
        await repository.RecordTerminalFailureAsync(jobId, FailureKind.Permanent, "gone", "source removed", DownloadJobState.FailedPermanent, null);

        notifier.Calls.Count.ShouldBe(2);
        notifier.Calls[0].ShouldBe((jobId, DownloadJobState.MetadataPending, DownloadJobState.Queued));
        notifier.Calls[1].ShouldBe((jobId, DownloadJobState.FailedPermanent, DownloadJobState.MetadataPending));
    }

    private sealed class CapturingStateNotifier : IDownloadJobStateNotifier
    {
        public List<(Guid JobId, DownloadJobState NewState, DownloadJobState PreviousState)> Calls { get; } = [];

        public Task NotifyAsync(Guid jobId, DownloadJobState newState, DownloadJobState previousState, Guid correlationId, CancellationToken ct = default)
        {
            Calls.Add((jobId, newState, previousState));
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
            SourceUrl = sourceUrl,
            RequestedBy = requestedBy,
            StorageKey = storageKey,
            SourceKind = sourceKind,
            IngestOrigin = IngestOrigin.Download
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
