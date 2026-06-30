using DataBridge.Messaging;
using DataBridge.Search;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentMigrator.Runner;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Npgsql;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace IntegrationTests.Maintenance;

public sealed class MediaDeleteExecutorTests
{
    private static readonly PostgresFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static MediaDeleteExecutorTests()
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
    public async Task DeleteMediaAsync_Deletes_Media_Requests_File_Deletes_And_Clears_Search()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [
            new ContentVersion("storage-a", "media/a.mp4", 1),
            new ContentVersion("storage-a", "media/a-2.mp4", 2)
        ]);
        var bus = new FakeMessageBus();
        var search = new RecordingSearchIndex();

        var response = await Fixture.CreateExecutor(bus, search).DeleteMediaAsync(mediaGuid);

        response.Success.ShouldBeTrue();
        response.MediaRemoved.ShouldBeTrue();
        response.FilesDeleted.ShouldBe(2);
        bus.DeleteRequests.Select(r => r.StoragePath).OrderBy(p => p)
            .ShouldBe(["media/a-2.mp4", "media/a.mp4"]);
        search.DeletedMedia.ShouldContain(mediaGuid.ToString());
        search.DeletedComments.ShouldContain(mediaGuid.ToString());
        search.DeletedCaptions.ShouldContain(mediaGuid.ToString());
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(0);
    }

    [Test]
    public async Task DeleteMediaAsync_Returns_NotFound_For_Unknown_Media()
    {
        var bus = new FakeMessageBus();
        var search = new RecordingSearchIndex();

        var response = await Fixture.CreateExecutor(bus, search).DeleteMediaAsync(Guid.NewGuid());

        response.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("not_found");
        bus.DeleteRequests.ShouldBeEmpty();
        search.DeletedMedia.ShouldBeEmpty();
    }

    [Test]
    public async Task DeleteMediaAsync_Returns_Conflict_When_Active_Download_Job_Exists()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        await Fixture.SeedLatestJobAsync(mediaGuid, "upload_pending");
        var bus = new FakeMessageBus();

        var response = await Fixture.CreateExecutor(bus, new RecordingSearchIndex()).DeleteMediaAsync(mediaGuid);

        response.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("conflict");
        bus.DeleteRequests.ShouldBeEmpty();
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
    }

    [Test]
    public async Task DeleteMediaAsync_Aborts_DB_Delete_When_Worker_Delete_Fails()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        var bus = new FakeMessageBus
        {
            DeleteResponse = new DeleteMediaFileResponse
            {
                Success = false,
                ErrorCode = "storage_error",
                ErrorMessage = "boom"
            }
        };
        var search = new RecordingSearchIndex();

        var response = await Fixture.CreateExecutor(bus, search).DeleteMediaAsync(mediaGuid);

        response.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("storage_error");
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        search.DeletedMedia.ShouldBeEmpty();
    }

    [Test]
    public async Task DeleteMediaForStorageKeyAsync_NonLast_Copy_Removes_Only_That_Key()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [
            new ContentVersion("storage-a", "media/a.mp4", 1),
            new ContentVersion("storage-b", "media/b.mp4", 2)
        ]);
        var bus = new FakeMessageBus();
        var search = new RecordingSearchIndex();

        var response = await Fixture.CreateExecutor(bus, search).DeleteMediaForStorageKeyAsync(mediaGuid, "storage-a");

        response.Success.ShouldBeTrue();
        response.MediaRemoved.ShouldBeFalse();
        response.FilesDeleted.ShouldBe(1);
        bus.DeleteRequests.Single().StoragePath.ShouldBe("media/a.mp4");
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
        (await Fixture.CountContentVersionsAsync(mediaGuid, "storage-a")).ShouldBe(0);
        (await Fixture.CountContentVersionsAsync(mediaGuid, "storage-b")).ShouldBe(1);
        search.DeletedMedia.ShouldBeEmpty();
    }

    [Test]
    public async Task DeleteMediaForStorageKeyAsync_Last_Copy_Cascades_To_Full_Delete()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [
            new ContentVersion("storage-a", "media/a.mp4", 1),
            new ContentVersion("storage-a", "media/a-2.mp4", 2)
        ]);
        var bus = new FakeMessageBus();
        var search = new RecordingSearchIndex();

        var response = await Fixture.CreateExecutor(bus, search).DeleteMediaForStorageKeyAsync(mediaGuid, "storage-a");

        response.Success.ShouldBeTrue();
        response.MediaRemoved.ShouldBeTrue();
        response.FilesDeleted.ShouldBe(2);
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(0);
        search.DeletedMedia.ShouldContain(mediaGuid.ToString());
    }

    [Test]
    public async Task DeleteMediaForStorageKeyAsync_Returns_NotFound_For_Unknown_Key()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);
        var bus = new FakeMessageBus();

        var response = await Fixture.CreateExecutor(bus, new RecordingSearchIndex())
            .DeleteMediaForStorageKeyAsync(mediaGuid, "storage-z");

        response.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("not_found");
        bus.DeleteRequests.ShouldBeEmpty();
        (await Fixture.CountAsync("media", "media_guid = @media_guid", mediaGuid)).ShouldBe(1);
    }

    [Test]
    public async Task DeleteMediaForStorageKeyAsync_Rejects_Blank_Storage_Key()
    {
        var mediaGuid = Guid.NewGuid();
        await Fixture.SeedMediaAsync(mediaGuid, [new ContentVersion("storage-a", "media/a.mp4", 1)]);

        var response = await Fixture.CreateExecutor(new FakeMessageBus(), new RecordingSearchIndex())
            .DeleteMediaForStorageKeyAsync(mediaGuid, "  ");

        response.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("validation");
    }

    private sealed record ContentVersion(string StorageKey, string StoragePath, int Version);

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private readonly IContainer _postgresContainer = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_DB", "froststream_media_delete_tests")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithPortBinding(5432, true)
            .Build();

        private NpgsqlDataSource? _dataSource;
        private bool _initialized;

        public Instant Now { get; } = Instant.FromUtc(2026, 6, 1, 0, 0);

        public MediaDeleteExecutor CreateExecutor(IMessageBus messageBus, ITypesenseIndexService searchIndex)
            => new(DataSource, messageBus, searchIndex, NullLogger<MediaDeleteExecutor>.Instance);

        private string ConnectionString =>
            new NpgsqlConnectionStringBuilder
            {
                Host = _postgresContainer.Hostname,
                Port = _postgresContainer.GetMappedPublicPort(5432),
                Database = "froststream_media_delete_tests",
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

            _dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
            _initialized = true;
        }

        public async Task ResetAsync()
        {
            await using var command = DataSource.CreateCommand(
                "TRUNCATE TABLE media, download_jobs RESTART IDENTITY CASCADE;");
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

        public async Task<long> CountContentVersionsAsync(Guid mediaGuid, string storageKey)
        {
            await using var command = DataSource.CreateCommand("""
                SELECT count(*)::bigint
                FROM media_content_id_versions
                WHERE media_guid = @media_guid AND storage_key = @storage_key;
                """);
            command.Parameters.AddWithValue("media_guid", mediaGuid);
            command.Parameters.AddWithValue("storage_key", storageKey);
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
                    .ScanIn(typeof(MediaDeleteExecutor).Assembly).For.Migrations());

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
        public DeleteMediaFileResponse? DeleteResponse { get; init; }
        public List<DeleteMediaFileRequest> DeleteRequests { get; } = [];

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
            if (request is DeleteMediaFileRequest delete)
            {
                DeleteRequests.Add(delete);
                var response = DeleteResponse ?? new DeleteMediaFileResponse { Success = true };
                return Task.FromResult((TResponse?)(object?)response);
            }

            throw new NotSupportedException(subject);
        }
    }

    private sealed class RecordingSearchIndex : ITypesenseIndexService
    {
        public List<string> DeletedMedia { get; } = [];
        public List<string> DeletedComments { get; } = [];
        public List<string> DeletedCaptions { get; } = [];

        public Task DeleteMediaByGuidAsync(string mediaGuid, CancellationToken ct = default)
        {
            DeletedMedia.Add(mediaGuid);
            return Task.CompletedTask;
        }

        public Task DeleteCommentsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default)
        {
            DeletedComments.Add(mediaGuid);
            return Task.CompletedTask;
        }

        public Task DeleteCaptionsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default)
        {
            DeletedCaptions.Add(mediaGuid);
            return Task.CompletedTask;
        }

        public Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> MediaCollectionHasFieldAsync(string fieldName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RecreateAllCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default) => throw new NotSupportedException();

        public Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default) => throw new NotSupportedException();

        public Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default) => throw new NotSupportedException();

        public Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
