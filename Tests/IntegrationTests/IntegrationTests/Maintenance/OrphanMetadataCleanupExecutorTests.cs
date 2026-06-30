using DataBridge.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NodaTime;
using Shouldly;
using Shared.Messaging;
using TUnit.Core;

namespace IntegrationTests.Maintenance;

public sealed class OrphanMetadataCleanupExecutorTests
{
    private static readonly PostgresFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static OrphanMetadataCleanupExecutorTests()
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
    public async Task CleanupAsync_Deletes_Media_When_All_Content_Has_Old_Unresolved_Missing_Findings()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [
            new ContentVersion("storage-a", "media/a.mp4", 1),
            new ContentVersion("storage-a", "media/a-2.mp4", 2)
        ]);
        await Fixture.SeedMediaBaseAsync(mediaGuid);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a-2.mp4", Fixture.OldFinding);

        var result = await Fixture.CreateExecutor().CleanupAsync(Fixture.Now);

        result.RecordedMetadataWithoutMediaCount.ShouldBe(2);
        result.DeletedMediaCount.ShouldBe(1);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(0);
        (await Fixture.CountAsync("metadata.media_base", "media_guid = @media_guid", mediaGuid)).ShouldBe(0);
        (await Fixture.CountOrphansAsync("metadata_without_media", "finalized")).ShouldBe(2);
    }

    [Test]
    public async Task CleanupAsync_Does_Not_Delete_When_Only_Some_Content_Is_Missing()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [
            new ContentVersion("storage-a", "media/a.mp4", 1),
            new ContentVersion("storage-a", "media/a-2.mp4", 2)
        ]);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);

        var result = await Fixture.CreateExecutor().CleanupAsync(Fixture.Now);

        result.RecordedMetadataWithoutMediaCount.ShouldBe(1);
        result.DeletedMediaCount.ShouldBe(0);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        (await Fixture.CountOrphansAsync("metadata_without_media", "detected")).ShouldBe(1);
    }

    [Test]
    public async Task CleanupAsync_Does_Not_Delete_When_Missing_Finding_Is_Too_New()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.NewFinding);

        var result = await Fixture.CreateExecutor().CleanupAsync(Fixture.Now);

        result.RecordedMetadataWithoutMediaCount.ShouldBe(1);
        result.DeletedMediaCount.ShouldBe(0);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        (await Fixture.CountOrphansAsync("metadata_without_media", "detected")).ShouldBe(1);
    }

    [Test]
    public async Task CleanupAsync_Does_Not_Delete_When_Missing_Finding_Is_Resolved()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        await Fixture.SeedMissingFindingAsync(
            mediaGuid,
            "storage-a",
            "media/a.mp4",
            Fixture.OldFinding,
            resolvedAt: Fixture.Now);

        var result = await Fixture.CreateExecutor().CleanupAsync(Fixture.Now);

        result.RecordedMetadataWithoutMediaCount.ShouldBe(0);
        result.DeletedMediaCount.ShouldBe(0);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
    }

    [Test]
    public async Task CleanupAsync_Does_Not_Delete_When_Active_Download_Job_Exists()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);
        await Fixture.SeedLatestJobAsync(mediaGuid, "upload_pending");

        var result = await Fixture.CreateExecutor().CleanupAsync(Fixture.Now);

        result.RecordedMetadataWithoutMediaCount.ShouldBe(1);
        result.DeletedMediaCount.ShouldBe(0);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        (await Fixture.CountOrphansAsync("metadata_without_media", "detected")).ShouldBe(1);
    }

    [Test]
    public async Task CleanupAsync_Records_And_Moves_Unexpected_Primary_Media_File()
    {
        await Fixture.SeedUnexpectedFindingAsync("storage-a", "loose/video.mp4", Fixture.OldFinding);
        await Fixture.SeedUnexpectedFindingAsync("storage-a", "loose/info.json", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            MoveResponse = new MoveOrphanedFileResponse { Success = true }
        };

        var result = await Fixture.CreateExecutor(bus).CleanupAsync(Fixture.Now);

        result.RecordedMediaWithoutMetadataCount.ShouldBe(1);
        result.MovedFileCount.ShouldBe(1);
        result.MoveFailedCount.ShouldBe(0);
        bus.MoveRequests.Count.ShouldBe(1);
        bus.MoveRequests[0].OriginalStoragePath.ShouldBe("loose/video.mp4");
        bus.MoveRequests[0].OrphanStoragePath.ShouldStartWith("orphaned/20260501/");
        (await Fixture.CountOrphansAsync("media_without_metadata", "moved")).ShouldBe(1);
        (await Fixture.CountOrphansAsync("media_without_metadata", "detected")).ShouldBe(0);
    }

    [Test]
    public async Task CleanupAsync_Does_Not_Move_File_Orphan_Before_Move_Timer_Elapses()
    {
        // Detected only 29 days ago; the default 30-day move timer has not yet elapsed.
        await Fixture.SeedUnexpectedFindingAsync("storage-a", "loose/video.mp4", Fixture.NewFinding);
        var bus = new FakeMessageBus
        {
            MoveResponse = new MoveOrphanedFileResponse { Success = true }
        };

        var result = await Fixture.CreateExecutor(bus).CleanupAsync(Fixture.Now);

        result.RecordedMediaWithoutMetadataCount.ShouldBe(1);
        result.MovedFileCount.ShouldBe(0);
        bus.MoveRequests.Count.ShouldBe(0);
        (await Fixture.CountOrphansAsync("media_without_metadata", "detected")).ShouldBe(1);
    }

    [Test]
    public async Task CleanupAsync_Does_Not_Purge_Moved_File_Before_Purge_Timer_Elapses()
    {
        // Moved 29 days ago; the default 30-day purge timer has not yet elapsed.
        await Fixture.SeedMovedFileOrphanAsync("storage-a", "loose/video.mp4", "orphaned/20260503/1/video.mp4", Fixture.NewFinding);
        var bus = new FakeMessageBus
        {
            DeleteResponse = new DeleteOrphanedFileResponse { Success = true }
        };

        var result = await Fixture.CreateExecutor(bus).CleanupAsync(Fixture.Now);

        result.DeletedFileCount.ShouldBe(0);
        bus.DeleteRequests.Count.ShouldBe(0);
        (await Fixture.CountOrphansAsync("media_without_metadata", "moved")).ShouldBe(1);
    }

    [Test]
    public async Task CleanupAsync_Records_But_Skips_Destructive_Stages_When_Disabled()
    {
        await Fixture.SetPolicyAsync(enabled: false, fileMoveAfterDays: 30, filePurgeAfterDays: 30, metadataDeleteAfterDays: 30);
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        await Fixture.SeedMediaBaseAsync(mediaGuid);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);
        await Fixture.SeedUnexpectedFindingAsync("storage-a", "loose/video.mp4", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            MoveResponse = new MoveOrphanedFileResponse { Success = true }
        };

        var result = await Fixture.CreateExecutor(bus).CleanupAsync(Fixture.Now);

        result.RecordedMetadataWithoutMediaCount.ShouldBe(1);
        result.RecordedMediaWithoutMetadataCount.ShouldBe(1);
        result.MovedFileCount.ShouldBe(0);
        result.DeletedMediaCount.ShouldBe(0);
        bus.MoveRequests.Count.ShouldBe(0);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        (await Fixture.CountOrphansAsync("media_without_metadata", "detected")).ShouldBe(1);
        (await Fixture.CountOrphansAsync("metadata_without_media", "detected")).ShouldBe(1);
    }

    [Test]
    public async Task UpdatePolicyAsync_RoundTrips_And_Validates()
    {
        var executor = Fixture.CreateExecutor();

        var updated = await executor.UpdatePolicyAsync(
            new OrphanCleanupPolicyUpdateRequest
            {
                Enabled = true,
                FileMoveAfterDays = 7,
                FilePurgeAfterDays = 90,
                MetadataDeleteAfterDays = 180,
                UpdatedBy = "admin"
            },
            Fixture.Now);

        updated.Success.ShouldBeTrue();
        updated.Policy!.Enabled.ShouldBeTrue();
        updated.Policy.FileMoveAfterDays.ShouldBe(7);
        updated.Policy.FilePurgeAfterDays.ShouldBe(90);
        updated.Policy.MetadataDeleteAfterDays.ShouldBe(180);
        updated.Policy.UpdatedBy.ShouldBe("admin");

        var fetched = await executor.GetPolicyAsync();
        fetched.Policy!.FileMoveAfterDays.ShouldBe(7);

        var invalid = await executor.UpdatePolicyAsync(
            new OrphanCleanupPolicyUpdateRequest
            {
                Enabled = true,
                FileMoveAfterDays = 0,
                FilePurgeAfterDays = 30,
                MetadataDeleteAfterDays = 30
            },
            Fixture.Now);

        invalid.Success.ShouldBeFalse();
        invalid.ErrorCode.ShouldBe("validation");
    }

    [Test]
    public async Task CleanupAsync_Deletes_Expired_Moved_File_Orphan()
    {
        await Fixture.SeedMovedFileOrphanAsync("storage-a", "loose/video.mp4", "orphaned/20260501/1/video.mp4", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            DeleteResponse = new DeleteOrphanedFileResponse { Success = true }
        };

        var result = await Fixture.CreateExecutor(bus).CleanupAsync(Fixture.Now);

        result.DeletedFileCount.ShouldBe(1);
        result.FileDeleteFailedCount.ShouldBe(0);
        bus.DeleteRequests.Count.ShouldBe(1);
        bus.DeleteRequests[0].OrphanStoragePath.ShouldBe("orphaned/20260501/1/video.mp4");
        (await Fixture.CountOrphansAsync("media_without_metadata", "finalized")).ShouldBe(1);
    }

    [Test]
    public async Task RestoreFileOrphanAsync_Restores_Moved_File_And_Resolves_Finding()
    {
        await Fixture.SeedUnexpectedFindingAsync("storage-a", "loose/video.mp4", Fixture.OldFinding);
        var orphanId = await Fixture.SeedMovedFileOrphanAsync("storage-a", "loose/video.mp4", "orphaned/20260501/1/video.mp4", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            RestoreResponse = new RestoreOrphanedFileResponse { Success = true }
        };

        var result = await Fixture.CreateExecutor(bus).RestoreFileOrphanAsync(orphanId, Fixture.Now);

        result.Success.ShouldBeTrue();
        bus.RestoreRequests.Count.ShouldBe(1);
        bus.RestoreRequests[0].OrphanStoragePath.ShouldBe("orphaned/20260501/1/video.mp4");
        (await Fixture.CountOrphansAsync("media_without_metadata", "resolved")).ShouldBe(1);
        (await Fixture.CountFindingsAsync("UnexpectedFile", "storage-a", "loose/video.mp4", resolved: true)).ShouldBe(1);
    }

    [Test]
    public async Task RestoreFileOrphanAsync_Refuses_Finalized_File_Orphan()
    {
        var orphanId = await Fixture.SeedMovedFileOrphanAsync("storage-a", "loose/video.mp4", "orphaned/20260501/1/video.mp4", Fixture.OldFinding);
        await Fixture.SetOrphanStateAsync(orphanId, "finalized", finalized: true);

        var result = await Fixture.CreateExecutor(new FakeMessageBus()).RestoreFileOrphanAsync(orphanId, Fixture.Now);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("conflict");
        (await Fixture.CountOrphansAsync("media_without_metadata", "finalized")).ShouldBe(1);
    }

    [Test]
    public async Task RestoreFileOrphanAsync_Refuses_When_Destination_Exists()
    {
        var orphanId = await Fixture.SeedMovedFileOrphanAsync("storage-a", "loose/video.mp4", "orphaned/20260501/1/video.mp4", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            RestoreResponse = new RestoreOrphanedFileResponse
            {
                Success = false,
                ErrorCode = "conflict",
                ErrorMessage = "Destination storage path already exists."
            }
        };

        var result = await Fixture.CreateExecutor(bus).RestoreFileOrphanAsync(orphanId, Fixture.Now);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("conflict");
        (await Fixture.CountOrphansAsync("media_without_metadata", "moved")).ShouldBe(1);
    }

    [Test]
    public async Task RestoreMetadataOrphanAsync_Restores_When_Expected_File_Exists()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        await Fixture.SeedMissingFindingAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);
        var orphanId = await Fixture.SeedMetadataOrphanAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            FileExistsResponse = new OrphanFileExistsResponse { Success = true, Exists = true }
        };

        var result = await Fixture.CreateExecutor(bus).RestoreMetadataOrphanAsync(orphanId, Fixture.Now);

        result.Success.ShouldBeTrue();
        bus.FileExistsRequests.Count.ShouldBe(1);
        (await Fixture.CountOrphansAsync("metadata_without_media", "resolved")).ShouldBe(1);
        (await Fixture.CountFindingsAsync("MissingFile", "storage-a", "media/a.mp4", resolved: true)).ShouldBe(1);
    }

    [Test]
    public async Task RestoreMetadataOrphanAsync_Refuses_When_Content_Version_Row_Is_Gone()
    {
        var mediaGuid = Guid.NewGuid();
        var orphanId = await Fixture.SeedMetadataOrphanAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);

        var result = await Fixture.CreateExecutor(new FakeMessageBus()).RestoreMetadataOrphanAsync(orphanId, Fixture.Now);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("conflict");
        (await Fixture.CountOrphansAsync("metadata_without_media", "detected")).ShouldBe(1);
    }

    [Test]
    public async Task RestoreMetadataOrphanAsync_Refuses_When_Expected_File_Is_Still_Missing()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        var orphanId = await Fixture.SeedMetadataOrphanAsync(mediaGuid, "storage-a", "media/a.mp4", Fixture.OldFinding);
        var bus = new FakeMessageBus
        {
            FileExistsResponse = new OrphanFileExistsResponse { Success = true, Exists = false }
        };

        var result = await Fixture.CreateExecutor(bus).RestoreMetadataOrphanAsync(orphanId, Fixture.Now);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("conflict");
        (await Fixture.CountOrphansAsync("metadata_without_media", "detected")).ShouldBe(1);
    }

    private sealed record ContentVersion(string StorageKey, string StoragePath, int Version);

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private readonly IContainer _postgresContainer = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_DB", "froststream_orphan_cleanup_tests")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithPortBinding(5432, true)
            .Build();

        private NpgsqlDataSource? _dataSource;
        private bool _initialized;

        public Instant Now { get; } = Instant.FromUtc(2026, 6, 1, 0, 0);
        public Instant Cutoff => Now.Minus(Duration.FromDays(30));
        public Instant OldFinding => Cutoff.Minus(Duration.FromDays(1));
        public Instant NewFinding => Cutoff.Plus(Duration.FromDays(1));
        public OrphanMetadataCleanupExecutor CreateExecutor(IMessageBus? messageBus = null)
            => messageBus is null ? new OrphanMetadataCleanupExecutor(DataSource) : new OrphanMetadataCleanupExecutor(DataSource, messageBus);

        private string ConnectionString =>
            new NpgsqlConnectionStringBuilder
            {
                Host = _postgresContainer.Hostname,
                Port = _postgresContainer.GetMappedPublicPort(5432),
                Database = "froststream_orphan_cleanup_tests",
                Username = "postgres",
                Password = "postgres",
                SearchPath = "storage,downloads,media,maintenance,metadata,auth,public"
            }.ConnectionString;

        private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            await _postgresContainer.StartAsync();
            await WaitForPostgresAsync();
            await RunMigrationsAsync();

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
            _dataSource = dataSourceBuilder.Build();
            _initialized = true;
        }

        public async Task ResetAsync()
        {
            await using var command = DataSource.CreateCommand(
                "TRUNCATE TABLE orphan_cleanup_items, filesystem_rescan_findings, media, download_jobs RESTART IDENTITY CASCADE;");
            await command.ExecuteNonQueryAsync();

            // Default seeded policy is disabled; enable it with the legacy 30-day timers so the
            // destructive cleanup stages run. Individual tests override this as needed.
            await SetPolicyAsync(enabled: true, fileMoveAfterDays: 30, filePurgeAfterDays: 30, metadataDeleteAfterDays: 30);
        }

        public async Task SetPolicyAsync(bool enabled, int fileMoveAfterDays, int filePurgeAfterDays, int metadataDeleteAfterDays)
        {
            await using var command = DataSource.CreateCommand("""
                INSERT INTO orphan_cleanup_policy
                    (id, enabled, file_move_after_days, file_purge_after_days, metadata_delete_after_days)
                VALUES
                    (1, @enabled, @move, @purge, @metadata)
                ON CONFLICT (id) DO UPDATE SET
                    enabled = EXCLUDED.enabled,
                    file_move_after_days = EXCLUDED.file_move_after_days,
                    file_purge_after_days = EXCLUDED.file_purge_after_days,
                    metadata_delete_after_days = EXCLUDED.metadata_delete_after_days;
                """);
            command.Parameters.AddWithValue("enabled", enabled);
            command.Parameters.AddWithValue("move", fileMoveAfterDays);
            command.Parameters.AddWithValue("purge", filePurgeAfterDays);
            command.Parameters.AddWithValue("metadata", metadataDeleteAfterDays);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedMediaAsync(Guid mediaGuid, IReadOnlyList<ContentVersion> contentVersions)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var command = new NpgsqlCommand(
                             "INSERT INTO media (media_guid, created_at) VALUES (@media_guid, @created_at);",
                             connection,
                             transaction))
            {
                command.Parameters.AddWithValue("media_guid", mediaGuid);
                command.Parameters.AddWithValue("created_at", Now.ToDateTimeOffset());
                await command.ExecuteNonQueryAsync();
            }

            foreach (var content in contentVersions)
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO media_content_id_versions
                        (media_guid, content_hash_xxh128, storage_key, storage_path, version_num)
                    VALUES
                        (@media_guid, @content_hash_xxh128, @storage_key, @storage_path, @version_num);
                    """, connection, transaction);
                command.Parameters.AddWithValue("media_guid", mediaGuid);
                command.Parameters.AddWithValue("content_hash_xxh128", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("storage_key", content.StorageKey);
                command.Parameters.AddWithValue("storage_path", content.StoragePath);
                command.Parameters.AddWithValue("version_num", content.Version);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async Task SeedMediaBaseAsync(Guid mediaGuid)
        {
            await using var command = DataSource.CreateCommand(
                "INSERT INTO metadata.media_base (media_guid, duration_ticks) VALUES (@media_guid, 1);");
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedMissingFindingAsync(
            Guid mediaGuid,
            string storageKey,
            string storagePath,
            Instant detectedAt,
            Instant? resolvedAt = null)
        {
            await using var command = DataSource.CreateCommand("""
                INSERT INTO filesystem_rescan_findings
                    (storage_key, storage_path, finding_type, media_guid, detected_at, last_seen_at, resolved_at)
                VALUES
                    (@storage_key, @storage_path, 'MissingFile', @media_guid, @detected_at, @last_seen_at, @resolved_at);
                """);
            command.Parameters.AddWithValue("storage_key", storageKey);
            command.Parameters.AddWithValue("storage_path", storagePath);
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            command.Parameters.AddWithValue("detected_at", detectedAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("last_seen_at", detectedAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("resolved_at", (object?)resolvedAt?.ToDateTimeOffset() ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SeedUnexpectedFindingAsync(
            string storageKey,
            string storagePath,
            Instant detectedAt,
            Instant? resolvedAt = null)
        {
            await using var command = DataSource.CreateCommand("""
                INSERT INTO filesystem_rescan_findings
                    (storage_key, storage_path, finding_type, media_guid, detected_at, last_seen_at, resolved_at)
                VALUES
                    (@storage_key, @storage_path, 'UnexpectedFile', NULL, @detected_at, @last_seen_at, @resolved_at);
                """);
            command.Parameters.AddWithValue("storage_key", storageKey);
            command.Parameters.AddWithValue("storage_path", storagePath);
            command.Parameters.AddWithValue("detected_at", detectedAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("last_seen_at", detectedAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("resolved_at", (object?)resolvedAt?.ToDateTimeOffset() ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> SeedMovedFileOrphanAsync(
            string storageKey,
            string originalStoragePath,
            string orphanStoragePath,
            Instant detectedAt)
        {
            await using var command = DataSource.CreateCommand("""
                INSERT INTO orphan_cleanup_items
                    (item_kind, state, storage_key, original_storage_path, orphan_storage_path, detected_at, last_seen_at, delete_after, moved_at, created_at, updated_at)
                VALUES
                    ('media_without_metadata', 'moved', @storage_key, @original_storage_path, @orphan_storage_path, @detected_at, @detected_at, @delete_after, @moved_at, @moved_at, @moved_at)
                RETURNING id;
                """);
            command.Parameters.AddWithValue("storage_key", storageKey);
            command.Parameters.AddWithValue("original_storage_path", originalStoragePath);
            command.Parameters.AddWithValue("orphan_storage_path", orphanStoragePath);
            command.Parameters.AddWithValue("detected_at", detectedAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("delete_after", detectedAt.Plus(Duration.FromDays(30)).ToDateTimeOffset());
            command.Parameters.AddWithValue("moved_at", detectedAt.ToDateTimeOffset());
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task<long> SeedMetadataOrphanAsync(
            Guid mediaGuid,
            string storageKey,
            string originalStoragePath,
            Instant detectedAt)
        {
            await using var command = DataSource.CreateCommand("""
                INSERT INTO orphan_cleanup_items
                    (item_kind, state, storage_key, original_storage_path, media_guid, detected_at, last_seen_at, delete_after, created_at, updated_at)
                VALUES
                    ('metadata_without_media', 'detected', @storage_key, @original_storage_path, @media_guid, @detected_at, @detected_at, @delete_after, @detected_at, @detected_at)
                RETURNING id;
                """);
            command.Parameters.AddWithValue("storage_key", storageKey);
            command.Parameters.AddWithValue("original_storage_path", originalStoragePath);
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            command.Parameters.AddWithValue("detected_at", detectedAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("delete_after", detectedAt.Plus(Duration.FromDays(30)).ToDateTimeOffset());
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task SetOrphanStateAsync(long orphanId, string state, bool finalized = false)
        {
            await using var command = DataSource.CreateCommand("""
                UPDATE orphan_cleanup_items
                SET state = @state,
                    finalized_at = CASE WHEN @finalized THEN @now ELSE finalized_at END,
                    updated_at = @now
                WHERE id = @id;
                """);
            command.Parameters.AddWithValue("id", orphanId);
            command.Parameters.AddWithValue("state", state);
            command.Parameters.AddWithValue("finalized", finalized);
            command.Parameters.AddWithValue("now", Now.ToDateTimeOffset());
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> CountFindingsAsync(string findingType, string storageKey, string storagePath, bool resolved)
        {
            await using var command = DataSource.CreateCommand("""
                SELECT count(*)::bigint
                FROM filesystem_rescan_findings
                WHERE finding_type = @finding_type
                  AND storage_key = @storage_key
                  AND storage_path = @storage_path
                  AND ((@resolved AND resolved_at IS NOT NULL) OR (NOT @resolved AND resolved_at IS NULL));
                """);
            command.Parameters.AddWithValue("finding_type", findingType);
            command.Parameters.AddWithValue("storage_key", storageKey);
            command.Parameters.AddWithValue("storage_path", storagePath);
            command.Parameters.AddWithValue("resolved", resolved);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task SeedLatestJobAsync(Guid mediaGuid, string state)
        {
            var jobId = Guid.NewGuid();
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var command = new NpgsqlCommand("""
                INSERT INTO download_jobs
                    (job_id, correlation_id, state, source_url, created_at, updated_at)
                VALUES
                    (@job_id, @correlation_id, @state::download_job_state, @source_url, @created_at, @updated_at);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("job_id", jobId);
                command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
                command.Parameters.AddWithValue("state", state);
                command.Parameters.AddWithValue("source_url", "https://example.test/watch?v=1");
                command.Parameters.AddWithValue("created_at", Now.ToDateTimeOffset());
                command.Parameters.AddWithValue("updated_at", Now.ToDateTimeOffset());
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = new NpgsqlCommand("""
                INSERT INTO media_source_versions
                    (provider, source_media_id, media_guid, latest_job_id)
                VALUES
                    ('test', @source_media_id, @media_guid, @latest_job_id);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("source_media_id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("media_guid", mediaGuid);
                command.Parameters.AddWithValue("latest_job_id", jobId);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async Task<long> CountAsync(string table, string whereClause, Guid mediaGuid)
        {
            await using var command = DataSource.CreateCommand($"SELECT count(*)::bigint FROM {table} WHERE {whereClause};");
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task<long> CountOrphansAsync(string itemKind, string state)
        {
            await using var command = DataSource.CreateCommand("""
                SELECT count(*)::bigint
                FROM orphan_cleanup_items
                WHERE item_kind = @item_kind
                  AND state = @state;
                """);
            command.Parameters.AddWithValue("item_kind", itemKind);
            command.Parameters.AddWithValue("state", state);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async ValueTask DisposeAsync()
        {
            if (_dataSource is not null)
            {
                await _dataSource.DisposeAsync();
            }

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
                    .ScanIn(typeof(OrphanMetadataCleanupExecutor).Assembly).For.Migrations());

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

    private sealed class FakeMessageBus : IMessageBus
    {
        public MoveOrphanedFileResponse? MoveResponse { get; init; }
        public DeleteOrphanedFileResponse? DeleteResponse { get; init; }
        public RestoreOrphanedFileResponse? RestoreResponse { get; init; }
        public OrphanFileExistsResponse? FileExistsResponse { get; init; }
        public List<MoveOrphanedFileRequest> MoveRequests { get; } = [];
        public List<DeleteOrphanedFileRequest> DeleteRequests { get; } = [];
        public List<RestoreOrphanedFileRequest> RestoreRequests { get; } = [];
        public List<OrphanFileExistsRequest> FileExistsRequests { get; } = [];

        public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ISubscription> SubscribeAsync<T>(
            string subject,
            Func<IMessageContext<T>, Task> handler,
            string? queueGroup = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TResponse?> RequestAsync<TRequest, TResponse>(
            string subject,
            TRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (request is MoveOrphanedFileRequest move)
            {
                MoveRequests.Add(move);
                return Task.FromResult((TResponse?)(object?)MoveResponse);
            }

            if (request is DeleteOrphanedFileRequest delete)
            {
                DeleteRequests.Add(delete);
                return Task.FromResult((TResponse?)(object?)DeleteResponse);
            }

            if (request is RestoreOrphanedFileRequest restore)
            {
                RestoreRequests.Add(restore);
                return Task.FromResult((TResponse?)(object?)RestoreResponse);
            }

            if (request is OrphanFileExistsRequest fileExists)
            {
                FileExistsRequests.Add(fileExists);
                return Task.FromResult((TResponse?)(object?)FileExistsResponse);
            }

            throw new NotSupportedException(subject);
        }
    }
}
