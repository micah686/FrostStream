using DataBridge.Application;
using DataBridge.Data;
using DataBridge.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Shared.Application;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.Lite;

/// <summary>
/// The transport-free subset of DataBridge that the Phase 8 Lite HTTP host can execute locally.
/// Deliberately excludes Cleipnir execution, NATS consumers, search indexing, and storage dispatch;
/// those modules are enabled by their owning Lite phases. Stage 10's transport-free execution
/// ledger is registered separately by the Lite composition root.
/// </summary>
public static class DataBridgeLiteServiceCollectionExtensions
{
    public static IServiceCollection AddDataBridgeLiteApiOperations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("froststreamdb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:froststreamdb is required by FrostStream Lite.");
        }

        services.AddDbContext<DataBridgeDbContext>(options =>
            options.UseNpgsql(connectionString, ConfigureNpgsql)
                .UseSnakeCaseNamingConvention());

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            return builder.Build();
        });
        services.AddSingleton<IClock>(SystemClock.Instance);

        services.AddScoped<IMetadataReadService, MetadataReadService>();
        services.AddScoped<IUserNotesRepository, UserNotesRepository>();
        services.AddScoped<IUserNoteApplication, UserNoteApplication>();
        services.AddScoped<IOptionPresetsRepository, OptionPresetsRepository>();
        services.AddScoped<CookieProfileApplication>();
        services.AddSingleton<IStorageConfigClient, LocalStorageConfigClient>();
        services.AddSingleton<IStoreProvider, CachingStoreProvider>();

        return services;
    }

    private static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder options) => options
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
        .MapEnum<PlaylistState>("playlist_state", "jobs");
}
