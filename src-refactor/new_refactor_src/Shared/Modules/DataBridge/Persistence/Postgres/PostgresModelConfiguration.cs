using Microsoft.EntityFrameworkCore;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.Persistence.Postgres;

internal static class PostgresModelConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<LocalStorageProtocol>("storage", "local_storage_protocol");
        modelBuilder.HasPostgresEnum<NetworkStorageProtocol>("storage", "network_storage_protocol");
        modelBuilder.HasPostgresEnum<S3CompatibleObjectStorageProvider>("storage", "s3_compatible_object_storage_provider");
        modelBuilder.HasPostgresEnum<AzureBlobCredentialMode>("storage", "azure_blob_credential_mode");
        modelBuilder.HasPostgresEnum<GoogleCloudStorageCredentialMode>("storage", "google_cloud_storage_credential_mode");
        modelBuilder.HasPostgresEnum<DownloadJobState>("jobs", "download_job_state");
        modelBuilder.HasPostgresEnum<DownloadJobStatus>("jobs", "download_job_status");
        modelBuilder.HasPostgresEnum<DownloadStage>("jobs", "download_stage");
        modelBuilder.HasPostgresEnum<DownloadStageStatus>("jobs", "download_stage_status");
        modelBuilder.HasPostgresEnum<DownloadGroupKind>("jobs", "download_group_kind");
        modelBuilder.HasPostgresEnum<DownloadGroupStatus>("jobs", "download_group_status");
        modelBuilder.HasPostgresEnum<DownloadArtifactStatus>("jobs", "download_artifact_status");
        modelBuilder.HasPostgresEnum<DownloadWorkerLeaseStatus>("jobs", "download_worker_lease_status");
        modelBuilder.HasPostgresEnum<FailureKind>("jobs", "failure_kind");
        modelBuilder.HasPostgresEnum<IngestOrigin>("media", "ingest_origin");
        modelBuilder.HasPostgresEnum<AudioRenditionStatus>("media", "audio_rendition_status");
        modelBuilder.HasPostgresEnum<StreamRenditionStatus>("media", "stream_rendition_status");
        modelBuilder.HasPostgresEnum<LocalImportStatus>("imports", "local_import_status");
        modelBuilder.HasPostgresEnum<ImportSessionStatus>("imports", "import_session_status");
        modelBuilder.HasPostgresEnum<ImportSessionSourceKind>("imports", "import_session_source_kind");
        modelBuilder.HasPostgresEnum<ImportSessionItemStatus>("imports", "import_session_item_status");
        modelBuilder.HasPostgresEnum<ImportSessionItemMetadataState>("imports", "import_session_item_metadata_state");
        modelBuilder.HasPostgresEnum<PlaylistState>("jobs", "playlist_state");

    }
}
