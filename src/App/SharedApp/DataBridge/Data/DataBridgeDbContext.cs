using DataBridge.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared.Database;

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
    public DbSet<DownloadGroupEntity> DownloadGroups => Set<DownloadGroupEntity>();
    public DbSet<DownloadJobRunEntity> DownloadJobRuns => Set<DownloadJobRunEntity>();
    public DbSet<DownloadStageAttemptEntity> DownloadStageAttempts => Set<DownloadStageAttemptEntity>();
    public DbSet<DownloadArtifactEntity> DownloadArtifacts => Set<DownloadArtifactEntity>();
    public DbSet<DownloadWorkerLeaseEntity> DownloadWorkerLeases => Set<DownloadWorkerLeaseEntity>();
    public DbSet<DownloadJobWarningEntity> DownloadJobWarnings => Set<DownloadJobWarningEntity>();
    public DbSet<DownloadJobHistoryEntity> DownloadJobHistory => Set<DownloadJobHistoryEntity>();
    public DbSet<DownloadJobProgressLogEntity> DownloadJobProgressLog => Set<DownloadJobProgressLogEntity>();
    public DbSet<FailedDownloadJobEntity> FailedDownloadJobs => Set<FailedDownloadJobEntity>();
    public DbSet<ProcessedMessageEntity> ProcessedMessages => Set<ProcessedMessageEntity>();
    public DbSet<MediaEntity> Media => Set<MediaEntity>();
    public DbSet<MediaSourceVersionEntity> MediaSourceVersions => Set<MediaSourceVersionEntity>();
    public DbSet<MediaContentIdVersionEntity> MediaContentIdVersions => Set<MediaContentIdVersionEntity>();
    public DbSet<AudioRenditionEntity> AudioRenditions => Set<AudioRenditionEntity>();
    public DbSet<StreamRenditionEntity> StreamRenditions => Set<StreamRenditionEntity>();
    public DbSet<MediaEncodingStatusEntity> MediaEncodingStatuses => Set<MediaEncodingStatusEntity>();
    public DbSet<LocalImportBatchEntity> LocalImportBatches => Set<LocalImportBatchEntity>();
    public DbSet<LocalImportItemEntity> LocalImportItems => Set<LocalImportItemEntity>();
    public DbSet<ImportSessionEntity> ImportSessions => Set<ImportSessionEntity>();
    public DbSet<ImportSessionItemEntity> ImportSessionItems => Set<ImportSessionItemEntity>();
    public DbSet<ImportSessionMappingEntity> ImportSessionMappings => Set<ImportSessionMappingEntity>();

    public DbSet<PlaylistEntity> Playlists => Set<PlaylistEntity>();
    public DbSet<PlaylistItemEntity> PlaylistItems => Set<PlaylistItemEntity>();
    public DbSet<PlaylistScanEntryEntity> PlaylistScanEntries => Set<PlaylistScanEntryEntity>();
    public DbSet<PlaylistSourceMetadataEntity> PlaylistSourceMetadata => Set<PlaylistSourceMetadataEntity>();
    public DbSet<MediaPlaylistMembershipEntity> MediaPlaylistMemberships => Set<MediaPlaylistMembershipEntity>();
    public DbSet<PlaylistMetadataEntity> PlaylistMetadata => Set<PlaylistMetadataEntity>();
    public DbSet<UserPlaylistEntity> UserPlaylists => Set<UserPlaylistEntity>();
    public DbSet<UserPlaylistItemEntity> UserPlaylistItems => Set<UserPlaylistItemEntity>();

    public DbSet<OptionPresetEntity> OptionPresets => Set<OptionPresetEntity>();
    public DbSet<DownloadConfigSetEntity> DownloadConfigSets => Set<DownloadConfigSetEntity>();
    public DbSet<ScheduledTaskEntity> ScheduledTasks => Set<ScheduledTaskEntity>();
    public DbSet<CreatorSourceEntity> CreatorSources => Set<CreatorSourceEntity>();
    public DbSet<CreatorScanStateEntity> CreatorScanStates => Set<CreatorScanStateEntity>();
    public DbSet<DiscoveredMediaEntity> DiscoveredMedia => Set<DiscoveredMediaEntity>();
    public DbSet<FrostStreamUserEntity> FrostStreamUsers => Set<FrostStreamUserEntity>();
    public DbSet<CookieProfileEntity> CookieProfiles => Set<CookieProfileEntity>();
    public DbSet<UserNoteEntity> UserNotes => Set<UserNoteEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaPlaylistMembershipEntity>(builder =>
        {
            builder.ToTable("media_playlist_membership", "playlists");
        });

        modelBuilder.Entity<PlaylistMetadataEntity>(builder =>
        {
            builder.ToTable("playlist_metadata", "playlists");
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataBridgeDbContext).Assembly);
        PersistenceModelConfiguration.Apply(modelBuilder, Database.IsSqlite());
        base.OnModelCreating(modelBuilder);
    }
}
