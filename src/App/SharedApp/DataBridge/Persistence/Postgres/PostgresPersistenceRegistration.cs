using DataBridge.Data;
using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.Persistence.Postgres;

internal static class PostgresPersistenceRegistration
{
    public static void AddPostgresPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<DataBridgeDbContext>(options =>
            options.UseNpgsql(
                    connectionString,
                    npgsqlOptions => npgsqlOptions
                        .UseNodaTime()
                        .MapEnum<LocalStorageProtocol>("local_storage_protocol", "storage")
                        .MapEnum<NetworkStorageProtocol>("network_storage_protocol", "storage")
                        .MapEnum<S3CompatibleObjectStorageProvider>("s3_compatible_object_storage_provider", "storage")
                        .MapEnum<AzureBlobCredentialMode>("azure_blob_credential_mode", "storage")
                        .MapEnum<GoogleCloudStorageCredentialMode>("google_cloud_storage_credential_mode", "storage")
                        .MapEnum<DownloadJobState>("download_job_state", "jobs")
                        .MapEnum<DownloadJobStatus>("download_job_status", "jobs")
                        .MapEnum<DownloadStage>("download_stage", "jobs")
                        .MapEnum<DownloadStageStatus>("download_stage_status", "jobs")
                        .MapEnum<DownloadGroupKind>("download_group_kind", "jobs")
                        .MapEnum<DownloadGroupStatus>("download_group_status", "jobs")
                        .MapEnum<DownloadArtifactStatus>("download_artifact_status", "jobs")
                        .MapEnum<DownloadWorkerLeaseStatus>("download_worker_lease_status", "jobs")
                        .MapEnum<FailureKind>("failure_kind", "jobs")
                        .MapEnum<IngestOrigin>("ingest_origin", "media")
                        .MapEnum<AudioRenditionStatus>("audio_rendition_status", "media")
                        .MapEnum<StreamRenditionStatus>("stream_rendition_status", "media")
                        .MapEnum<LocalImportStatus>("local_import_status", "imports")
                        .MapEnum<ImportSessionStatus>("import_session_status", "imports")
                        .MapEnum<ImportSessionSourceKind>("import_session_source_kind", "imports")
                        .MapEnum<ImportSessionItemStatus>("import_session_item_status", "imports")
                        .MapEnum<ImportSessionItemMetadataState>("import_session_item_metadata_state", "imports")
                        .MapEnum<PlaylistState>("playlist_state", "jobs"))
                .UseSnakeCaseNamingConvention());

        services
            .AddFluentMigratorCore()
            .ConfigureRunner(runnerBuilder => runnerBuilder
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(DataBridgeModule).Assembly).For.Migrations());

        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddScoped<IApplicationSchemaInitializer, PostgresSchemaInitializer>();
    }
}
