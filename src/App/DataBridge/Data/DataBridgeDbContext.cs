using Microsoft.EntityFrameworkCore;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.Data;

public sealed class DataBridgeDbContext(DbContextOptions<DataBridgeDbContext> options) : DbContext(options)
{
    public DbSet<StorageConfigEntity> StorageConfigs => Set<StorageConfigEntity>();
    public DbSet<StorageLocalConfigEntity> StorageLocalConfigs => Set<StorageLocalConfigEntity>();
    public DbSet<StorageNetworkConfigEntity> StorageNetworkConfigs => Set<StorageNetworkConfigEntity>();
    public DbSet<StorageS3CompatibleObjectConfigEntity> StorageS3CompatibleObjectConfigs => Set<StorageS3CompatibleObjectConfigEntity>();
    public DbSet<StorageAzureBlobObjectConfigEntity> StorageAzureBlobObjectConfigs => Set<StorageAzureBlobObjectConfigEntity>();
    public DbSet<StorageGoogleCloudStorageObjectConfigEntity> StorageGoogleCloudStorageObjectConfigs => Set<StorageGoogleCloudStorageObjectConfigEntity>();

    public DbSet<DownloadJobEntity> DownloadJobs => Set<DownloadJobEntity>();
    public DbSet<DownloadJobHistoryEntity> DownloadJobHistory => Set<DownloadJobHistoryEntity>();
    public DbSet<DownloadJobProgressLogEntity> DownloadJobProgressLog => Set<DownloadJobProgressLogEntity>();
    public DbSet<FailedDownloadJobEntity> FailedDownloadJobs => Set<FailedDownloadJobEntity>();
    public DbSet<ProcessedMessageEntity> ProcessedMessages => Set<ProcessedMessageEntity>();
    public DbSet<MediaEntity> Media => Set<MediaEntity>();
    public DbSet<MediaSourceVersionEntity> MediaSourceVersions => Set<MediaSourceVersionEntity>();
    public DbSet<MediaContentIdVersionEntity> MediaContentIdVersions => Set<MediaContentIdVersionEntity>();
    public DbSet<AudioRenditionEntity> AudioRenditions => Set<AudioRenditionEntity>();
    public DbSet<StreamRenditionEntity> StreamRenditions => Set<StreamRenditionEntity>();
    public DbSet<LocalImportBatchEntity> LocalImportBatches => Set<LocalImportBatchEntity>();
    public DbSet<LocalImportItemEntity> LocalImportItems => Set<LocalImportItemEntity>();
    public DbSet<ImportSessionEntity> ImportSessions => Set<ImportSessionEntity>();
    public DbSet<ImportSessionItemEntity> ImportSessionItems => Set<ImportSessionItemEntity>();
    public DbSet<ImportSessionMappingEntity> ImportSessionMappings => Set<ImportSessionMappingEntity>();

    public DbSet<PlaylistEntity> Playlists => Set<PlaylistEntity>();
    public DbSet<PlaylistItemEntity> PlaylistItems => Set<PlaylistItemEntity>();
    public DbSet<PlaylistScanEntryEntity> PlaylistScanEntries => Set<PlaylistScanEntryEntity>();
    public DbSet<MediaPlaylistMembershipEntity> MediaPlaylistMemberships => Set<MediaPlaylistMembershipEntity>();
    public DbSet<PlaylistMetadataEntity> PlaylistMetadata => Set<PlaylistMetadataEntity>();
    public DbSet<UserPlaylistEntity> UserPlaylists => Set<UserPlaylistEntity>();
    public DbSet<UserPlaylistItemEntity> UserPlaylistItems => Set<UserPlaylistItemEntity>();

    public DbSet<OptionPresetEntity> OptionPresets => Set<OptionPresetEntity>();
    public DbSet<DownloadConfigSetEntity> DownloadConfigSets => Set<DownloadConfigSetEntity>();
    public DbSet<ScheduledTaskEntity> ScheduledTasks => Set<ScheduledTaskEntity>();
    public DbSet<FilesystemRescanFindingEntity> FilesystemRescanFindings => Set<FilesystemRescanFindingEntity>();
    public DbSet<CreatorSourceEntity> CreatorSources => Set<CreatorSourceEntity>();
    public DbSet<DiscoveredMediaEntity> DiscoveredMedia => Set<DiscoveredMediaEntity>();
    public DbSet<FrostStreamUserEntity> FrostStreamUsers => Set<FrostStreamUserEntity>();
    public DbSet<CookieProfileEntity> CookieProfiles => Set<CookieProfileEntity>();
    public DbSet<UserNoteEntity> UserNotes => Set<UserNoteEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<LocalStorageProtocol>("storage", "local_storage_protocol");
        modelBuilder.HasPostgresEnum<NetworkStorageProtocol>("storage", "network_storage_protocol");
        modelBuilder.HasPostgresEnum<S3CompatibleObjectStorageProvider>("storage", "s3_compatible_object_storage_provider");
        modelBuilder.HasPostgresEnum<AzureBlobCredentialMode>("storage", "azure_blob_credential_mode");
        modelBuilder.HasPostgresEnum<GoogleCloudStorageCredentialMode>("storage", "google_cloud_storage_credential_mode");
        modelBuilder.HasPostgresEnum<DownloadJobState>("downloads", "download_job_state");
        modelBuilder.HasPostgresEnum<FailureKind>("downloads", "failure_kind");
        modelBuilder.HasPostgresEnum<IngestOrigin>("media", "ingest_origin");
        modelBuilder.HasPostgresEnum<AudioRenditionStatus>("media", "audio_rendition_status");
        modelBuilder.HasPostgresEnum<StreamRenditionStatus>("media", "stream_rendition_status");
        modelBuilder.HasPostgresEnum<LocalImportStatus>("imports", "local_import_status");
        modelBuilder.HasPostgresEnum<ImportSessionStatus>("imports", "import_session_status");
        modelBuilder.HasPostgresEnum<ImportSessionSourceKind>("imports", "import_session_source_kind");
        modelBuilder.HasPostgresEnum<ImportSessionItemStatus>("imports", "import_session_item_status");
        modelBuilder.HasPostgresEnum<ImportSessionItemMetadataState>("imports", "import_session_item_metadata_state");
        modelBuilder.HasPostgresEnum<PlaylistState>("playlists", "playlist_state");

        modelBuilder.Entity<MediaPlaylistMembershipEntity>(builder =>
        {
            builder.ToTable("media_playlist_membership", "playlists");
        });

        modelBuilder.Entity<PlaylistMetadataEntity>(builder =>
        {
            builder.ToTable("playlist_metadata", "playlists");
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataBridgeDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
