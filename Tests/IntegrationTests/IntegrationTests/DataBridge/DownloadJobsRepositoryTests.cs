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

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private readonly IContainer _postgresContainer = new ContainerBuilder()
            .WithImage("postgres:17")
            .WithEnvironment("POSTGRES_DB", "froststream_download_jobs_repository_tests")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithPortBinding(5432, true)
            .Build();

        private bool _initialized;

        public Instant Now { get; } = Instant.FromUtc(2026, 6, 1, 0, 0);

        private string ConnectionString =>
            $"Host=127.0.0.1;Port={_postgresContainer.GetMappedPublicPort(5432)};Database=froststream_download_jobs_repository_tests;Username=postgres;Password=postgres";

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
                        .MapEnum<LocalStorageProtocol>("local_storage_protocol")
                        .MapEnum<NetworkStorageProtocol>("network_storage_protocol")
                        .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider")
                        .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode")
                        .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode")
                        .MapEnum<DownloadJobState>("download_job_state")
                        .MapEnum<FailureKind>("failure_kind")
                        .MapEnum<PlaylistState>("playlist_state"))
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
