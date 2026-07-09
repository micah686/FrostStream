namespace WebAPI.Auth;

/// <summary>
/// Seeded baseline capability_group ids. These bundles are system-owned and immutable at runtime;
/// a redeploy always restores them. Runtime-composed bundles use the <c>user.</c> id prefix instead
/// (see <see cref="Shared.Auth.AuthConstants.UserBundlePrefix"/>).
/// </summary>
public static class Bundles
{
    public const string Downloading = "downloading";
    public const string Storage = "storage";
    public const string Metadata = "metadata";
    public const string MetadataAdmin = "metadata-admin";
    public const string Playlists = "playlists";
    public const string Cookies = "cookies";
    public const string Schedules = "schedules";
    public const string Presets = "presets";
    public const string DownloadConfigSets = "download-config-sets";
    public const string CreatorSources = "creator-sources";
    public const string Media = "media";
    public const string Notifications = "notifications";
    public const string Management = "management";
    public const string MediaAccessAdmin = "media-access-admin";
}

/// <summary>
/// Stable endpoint ids. Each id maps 1:1 to a real route and is referenced both by the
/// <c>[Endpoint]</c> attribute on the action and by <see cref="EndpointCatalog"/>. Ids are explicit
/// (never convention-derived) so renaming a controller/action never silently orphans its tuples.
/// </summary>
public static class EndpointIds
{
    // Downloads
    public const string DownloadsCreate = "downloads.create";
    public const string DownloadsAudio = "downloads.audio";
    public const string DownloadsPreset = "downloads.preset";
    public const string DownloadsUpdatePriority = "downloads.update-priority";
    public const string DownloadsCancel = "downloads.cancel";
    public const string DownloadsRestartHalted = "downloads.restart-halted";
    public const string DownloadsQueueList = "downloads.queue.list";
    public const string DownloadsQueueGet = "downloads.queue.get";
    public const string DownloadsQueueHistory = "downloads.queue.history";
    public const string DownloadsQueueStream = "downloads.queue.stream";
    public const string DownloadsQueueProgress = "downloads.queue.progress";
    public const string DownloadsQueuePriority = "downloads.queue.priority";
    public const string DownloadsQueueCancel = "downloads.queue.cancel";
    public const string DownloadsQueueRestart = "downloads.queue.restart";
    public const string ImportsSessionsCreate = "imports.sessions.create";
    public const string ImportsSessionsList = "imports.sessions.list";
    public const string ImportsSessionsGet = "imports.sessions.get";
    public const string ImportsSessionsItemsList = "imports.sessions.items.list";
    public const string ImportsSessionsItemsPatch = "imports.sessions.items.patch";
    public const string ImportsSessionsItemsBulk = "imports.sessions.items.bulk";
    public const string ImportsSessionsMapping = "imports.sessions.mapping";
    public const string ImportsSessionsEnrich = "imports.sessions.enrich";
    public const string ImportsSessionsCommit = "imports.sessions.commit";
    public const string ImportsSessionsRetry = "imports.sessions.retry";
    public const string ImportsSessionsCancel = "imports.sessions.cancel";

    // Storage
    public const string StorageLocalCreate = "storage.local.create";
    public const string StorageLocalUpdate = "storage.local.update";
    public const string StorageNetworkCreate = "storage.network.create";
    public const string StorageNetworkUpdate = "storage.network.update";
    public const string StorageS3Create = "storage.s3.create";
    public const string StorageS3Update = "storage.s3.update";
    public const string StorageAzureCreate = "storage.azure.create";
    public const string StorageAzureUpdate = "storage.azure.update";
    public const string StorageGcsCreate = "storage.gcs.create";
    public const string StorageGcsUpdate = "storage.gcs.update";
    public const string StorageList = "storage.list";
    public const string StorageDelete = "storage.delete";
    public const string StorageGet = "storage.get";

    // Metadata (read)
    public const string MetadataList = "metadata.list";
    public const string MetadataSearch = "metadata.search";
    public const string MetadataGet = "metadata.get";
    public const string MetadataTechnical = "metadata.technical";
    public const string MetadataVersions = "metadata.versions";
    public const string MetadataComments = "metadata.comments";
    public const string MetadataCaptions = "metadata.captions";
    public const string MetadataAccountsList = "metadata.accounts.list";
    public const string MetadataAccountsGet = "metadata.accounts.get";
    public const string MetadataAccountsRefreshAssets = "metadata.accounts.refresh-assets";
    public const string MetadataAccountsMedia = "metadata.accounts.media";
    public const string MetadataTaxonomyTags = "metadata.taxonomy.tags";
    public const string MetadataTaxonomyCategories = "metadata.taxonomy.categories";
    public const string MetadataTaxonomyGenres = "metadata.taxonomy.genres";
    public const string StatisticsOverview = "statistics.overview";
    public const string StatisticsChannelsList = "statistics.channels.list";
    public const string StatisticsChannelsGet = "statistics.channels.get";
    public const string StatisticsDownloadHistory = "statistics.download-history";
    public const string UserNotesUpsert = "user-notes.upsert";
    public const string UserNotesGet = "user-notes.get";
    public const string UserNotesDelete = "user-notes.delete";
    public const string UserNotesList = "user-notes.list";
    public const string UserNotesSearch = "user-notes.search";

    // Unified search
    public const string SearchQuery = "search.query";
    public const string SearchSimilar = "search.similar";

    // Metadata (admin)
    public const string MetadataReindex = "metadata.reindex";
    public const string MetadataOrphansList = "metadata.orphans.list";
    public const string MetadataOrphansRestoreFile = "metadata.orphans.restore-file";
    public const string MetadataOrphansRestoreMetadata = "metadata.orphans.restore-metadata";
    public const string OrphanCleanupPolicyGet = "metadata.orphan-cleanup-policy.get";
    public const string OrphanCleanupPolicyUpdate = "metadata.orphan-cleanup-policy.update";
    public const string MediaDelete = "media.delete";
    public const string MediaDeleteForStorageKey = "media.delete-for-key";
    public const string WatchedAutoDeletePolicyGet = "watched-auto-delete.policy.get";
    public const string WatchedAutoDeletePolicyUpdate = "watched-auto-delete.policy.update";
    public const string WatchedAutoDeleteRun = "watched-auto-delete.run";

    // Playlists
    public const string PlaylistsCreate = "playlists.create";
    public const string PlaylistsList = "playlists.list";
    public const string PlaylistsGet = "playlists.get";
    public const string PlaylistsForceQueueItem = "playlists.force-queue-item";
    public const string UserPlaylistsCreate = "user-playlists.create";
    public const string UserPlaylistsList = "user-playlists.list";
    public const string UserPlaylistsGet = "user-playlists.get";
    public const string UserPlaylistsUpdate = "user-playlists.update";
    public const string UserPlaylistsDelete = "user-playlists.delete";
    public const string UserPlaylistsAddItem = "user-playlists.add-item";
    public const string UserPlaylistsRemoveItem = "user-playlists.remove-item";
    public const string UserPlaylistsReorderItems = "user-playlists.reorder-items";

    // Cookies
    public const string CookiesPut = "cookies.put";
    public const string CookiesList = "cookies.list";
    public const string CookiesGet = "cookies.get";
    public const string CookiesDelete = "cookies.delete";

    // Notifications
    public const string NotificationsPreferencesGet = "notifications.preferences.get";
    public const string NotificationsPreferencesUpdate = "notifications.preferences.update";
    public const string NotificationsProvidersList = "notifications.providers.list";
    public const string NotificationsProvidersGet = "notifications.providers.get";
    public const string NotificationsProvidersUpsert = "notifications.providers.upsert";
    public const string NotificationsProvidersDelete = "notifications.providers.delete";
    public const string NotificationsSecretsUpsert = "notifications.secrets.upsert";
    public const string NotificationsSecretsDelete = "notifications.secrets.delete";
    public const string NotificationsTest = "notifications.test";

    // Schedules
    public const string SchedulesCreate = "schedules.create";
    public const string SchedulesUpdate = "schedules.update";
    public const string SchedulesGet = "schedules.get";
    public const string SchedulesList = "schedules.list";
    public const string SchedulesDelete = "schedules.delete";

    // Option presets
    public const string OptionPresetsCreate = "option-presets.create";
    public const string OptionPresetsUpdate = "option-presets.update";
    public const string OptionPresetsGet = "option-presets.get";
    public const string OptionPresetsList = "option-presets.list";
    public const string OptionPresetsDelete = "option-presets.delete";

    // Download config sets
    public const string DownloadConfigSetsCreate = "download-config-sets.create";
    public const string DownloadConfigSetsUpdate = "download-config-sets.update";
    public const string DownloadConfigSetsGet = "download-config-sets.get";
    public const string DownloadConfigSetsList = "download-config-sets.list";
    public const string DownloadConfigSetsDelete = "download-config-sets.delete";

    // Creator sources
    public const string CreatorSourcesCreate = "creator-sources.create";
    public const string CreatorSourcesDownloadChannel = "creator-sources.download-channel";
    public const string CreatorSourcesUpdate = "creator-sources.update";
    public const string CreatorSourcesGet = "creator-sources.get";
    public const string CreatorSourcesList = "creator-sources.list";
    public const string CreatorSourcesRefreshAssets = "creator-sources.refresh-assets";
    public const string CreatorSourcesScanNow = "creator-sources.scan-now";
    public const string CreatorSourcesDelete = "creator-sources.delete";
    public const string CreatorSourcesListIgnoredMedia = "creator-sources.list-ignored-media";
    public const string CreatorSourcesForceQueueMedia = "creator-sources.force-queue-media";

    // Media
    public const string MediaStream = "media.stream";
    public const string MediaThumbnail = "media.thumbnail";
    public const string MediaCaption = "media.caption";
    public const string MediaAccountAsset = "media.account-asset";
    public const string MediaCastToken = "media.cast-token";
    public const string MediaHlsManifest = "media.hls-manifest";
    public const string MediaHlsSegment = "media.hls-segment";
    public const string MediaWatchStateGet = "media.watch-state.get";
    public const string MediaWatchStateUpsert = "media.watch-state.upsert";
    public const string MediaWatchStateListInProgress = "media.watch-state.list-in-progress";
    public const string MediaWatchStateListHistory = "media.watch-state.list-history";
    public const string MediaWatchStateMarkWatched = "media.watch-state.mark-watched";
    public const string MediaWatchStateMarkUnwatched = "media.watch-state.mark-unwatched";
    public const string MediaLikeStateGet = "media.like-state.get";
    public const string MediaLike = "media.like";
    public const string MediaUnlike = "media.unlike";
    public const string MediaLikesList = "media.likes.list";
    public const string PlaylistAudioStream = "playlists.audio-stream";

    // Server-side casting (protocol providers driven by the server via local discovery)
    public const string CastDevicesList = "cast.devices.list";
    public const string CastSessionsStart = "cast.sessions.start";
    public const string CastSessionsList = "cast.sessions.list";
    public const string CastSessionsGet = "cast.sessions.get";
    public const string CastSessionsPlay = "cast.sessions.play";
    public const string CastSessionsPause = "cast.sessions.pause";
    public const string CastSessionsStop = "cast.sessions.stop";
    public const string CastSessionsSeek = "cast.sessions.seek";
    public const string CastSessionsVolume = "cast.sessions.volume";
    public const string CastSessionsDisconnect = "cast.sessions.disconnect";
    public const string CastSessionsEvents = "cast.sessions.events";

    // Media access control (watch-time restrictions). The internal `media-access.check` gate has no
    // endpoint id — it is server-to-server only and never reachable as a route.
    public const string MediaAccessMediaList = "media-access.media.list";
    public const string MediaAccessMediaAdd = "media-access.media.add";
    public const string MediaAccessMediaRemove = "media-access.media.remove";
    public const string MediaAccessMediaClear = "media-access.media.clear";
    public const string MediaAccessProviderList = "media-access.provider.list";
    public const string MediaAccessProviderAdd = "media-access.provider.add";
    public const string MediaAccessProviderRemove = "media-access.provider.remove";
    public const string MediaAccessProviderClear = "media-access.provider.clear";
    public const string MediaAccessAgeList = "media-access.age.list";
    public const string MediaAccessAgeAdd = "media-access.age.add";
    public const string MediaAccessAgeRemove = "media-access.age.remove";

    // Bundle management (runtime). These live in the `:all` bootstrap bundle so the bootstrap admin
    // can always reach them — see the lock-out guard in B_Axis1.MD.
    public const string ManagementCatalog = "management.catalog";
    public const string ManagementBundlesList = "management.bundles.list";
    public const string ManagementBundlesGet = "management.bundles.get";
    public const string ManagementBundlesCreate = "management.bundles.create";
    public const string ManagementBundlesSetEndpoints = "management.bundles.set-endpoints";
    public const string ManagementBundlesDelete = "management.bundles.delete";
    public const string ManagementGrantsCreate = "management.grants.create";
    public const string ManagementGrantsDelete = "management.grants.delete";

    // Backups
    public const string BackupsCreate = "backups.create";
    public const string BackupsJobsList = "backups.jobs.list";
    public const string BackupsJobsGet = "backups.jobs.get";
    public const string BackupsList = "backups.list";
    public const string BackupsVerify = "backups.verify";
    public const string BackupsRestorePlan = "backups.restore-plan";
}

public sealed record EndpointDefinition(string Id, string Bundle);

/// <summary>
/// The single source-of-truth registry of every API endpoint and its seeded baseline bundle. It is
/// (1) the seed source for the provisioner, (2) the drift guard between this list, the
/// <c>[Endpoint]</c> attributes, and the OpenFGA model, and (3) the catalog the runtime management
/// surface lists so user-composed bundles can only reference real routes.
/// </summary>
public static class EndpointCatalog
{
    public static readonly IReadOnlyList<EndpointDefinition> Endpoints =
    [
        new(EndpointIds.DownloadsCreate, Bundles.Downloading),
        new(EndpointIds.DownloadsAudio, Bundles.Downloading),
        new(EndpointIds.DownloadsPreset, Bundles.Downloading),
        new(EndpointIds.DownloadsUpdatePriority, Bundles.Downloading),
        new(EndpointIds.DownloadsCancel, Bundles.Downloading),
        new(EndpointIds.DownloadsRestartHalted, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueList, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueGet, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueHistory, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueStream, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueProgress, Bundles.Downloading),
        new(EndpointIds.DownloadsQueuePriority, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueCancel, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueRestart, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCreate, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsList, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsGet, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsItemsList, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsItemsPatch, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsItemsBulk, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsMapping, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsEnrich, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCommit, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsRetry, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCancel, Bundles.Downloading),

        new(EndpointIds.StorageLocalCreate, Bundles.Storage),
        new(EndpointIds.StorageLocalUpdate, Bundles.Storage),
        new(EndpointIds.StorageNetworkCreate, Bundles.Storage),
        new(EndpointIds.StorageNetworkUpdate, Bundles.Storage),
        new(EndpointIds.StorageS3Create, Bundles.Storage),
        new(EndpointIds.StorageS3Update, Bundles.Storage),
        new(EndpointIds.StorageAzureCreate, Bundles.Storage),
        new(EndpointIds.StorageAzureUpdate, Bundles.Storage),
        new(EndpointIds.StorageGcsCreate, Bundles.Storage),
        new(EndpointIds.StorageGcsUpdate, Bundles.Storage),
        new(EndpointIds.StorageList, Bundles.Storage),
        new(EndpointIds.StorageDelete, Bundles.Storage),
        new(EndpointIds.StorageGet, Bundles.Storage),

        new(EndpointIds.MetadataList, Bundles.Metadata),
        new(EndpointIds.MetadataSearch, Bundles.Metadata),
        new(EndpointIds.MetadataGet, Bundles.Metadata),
        new(EndpointIds.MetadataTechnical, Bundles.Metadata),
        new(EndpointIds.MetadataVersions, Bundles.Metadata),
        new(EndpointIds.MetadataComments, Bundles.Metadata),
        new(EndpointIds.MetadataCaptions, Bundles.Metadata),
        new(EndpointIds.MetadataAccountsList, Bundles.Metadata),
        new(EndpointIds.MetadataAccountsGet, Bundles.Metadata),
        new(EndpointIds.MetadataAccountsMedia, Bundles.Metadata),
        new(EndpointIds.MetadataTaxonomyTags, Bundles.Metadata),
        new(EndpointIds.MetadataTaxonomyCategories, Bundles.Metadata),
        new(EndpointIds.MetadataTaxonomyGenres, Bundles.Metadata),
        new(EndpointIds.StatisticsOverview, Bundles.Metadata),
        new(EndpointIds.StatisticsChannelsList, Bundles.Metadata),
        new(EndpointIds.StatisticsChannelsGet, Bundles.Metadata),
        new(EndpointIds.StatisticsDownloadHistory, Bundles.Metadata),
        new(EndpointIds.UserNotesUpsert, Bundles.Metadata),
        new(EndpointIds.UserNotesGet, Bundles.Metadata),
        new(EndpointIds.UserNotesDelete, Bundles.Metadata),
        new(EndpointIds.UserNotesList, Bundles.Metadata),
        new(EndpointIds.UserNotesSearch, Bundles.Metadata),
        new(EndpointIds.SearchQuery, Bundles.Metadata),
        new(EndpointIds.SearchSimilar, Bundles.Metadata),

        new(EndpointIds.MetadataReindex, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataOrphansList, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataOrphansRestoreFile, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataOrphansRestoreMetadata, Bundles.MetadataAdmin),
        new(EndpointIds.OrphanCleanupPolicyGet, Bundles.MetadataAdmin),
        new(EndpointIds.OrphanCleanupPolicyUpdate, Bundles.MetadataAdmin),
        new(EndpointIds.MediaDelete, Bundles.MetadataAdmin),
        new(EndpointIds.MediaDeleteForStorageKey, Bundles.MetadataAdmin),
        new(EndpointIds.WatchedAutoDeletePolicyGet, Bundles.MetadataAdmin),
        new(EndpointIds.WatchedAutoDeletePolicyUpdate, Bundles.MetadataAdmin),
        new(EndpointIds.WatchedAutoDeleteRun, Bundles.MetadataAdmin),

        new(EndpointIds.PlaylistsCreate, Bundles.Playlists),
        new(EndpointIds.PlaylistsList, Bundles.Playlists),
        new(EndpointIds.PlaylistsGet, Bundles.Playlists),
        new(EndpointIds.PlaylistsForceQueueItem, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsCreate, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsList, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsGet, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsUpdate, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsDelete, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsAddItem, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsRemoveItem, Bundles.Playlists),
        new(EndpointIds.UserPlaylistsReorderItems, Bundles.Playlists),

        new(EndpointIds.CookiesPut, Bundles.Cookies),
        new(EndpointIds.CookiesList, Bundles.Cookies),
        new(EndpointIds.CookiesGet, Bundles.Cookies),
        new(EndpointIds.CookiesDelete, Bundles.Cookies),

        new(EndpointIds.NotificationsPreferencesGet, Bundles.Notifications),
        new(EndpointIds.NotificationsPreferencesUpdate, Bundles.Notifications),
        new(EndpointIds.NotificationsProvidersList, Bundles.Notifications),
        new(EndpointIds.NotificationsProvidersGet, Bundles.Notifications),
        new(EndpointIds.NotificationsProvidersUpsert, Bundles.Notifications),
        new(EndpointIds.NotificationsProvidersDelete, Bundles.Notifications),
        new(EndpointIds.NotificationsSecretsUpsert, Bundles.Notifications),
        new(EndpointIds.NotificationsSecretsDelete, Bundles.Notifications),
        new(EndpointIds.NotificationsTest, Bundles.Notifications),

        new(EndpointIds.SchedulesCreate, Bundles.Schedules),
        new(EndpointIds.SchedulesUpdate, Bundles.Schedules),
        new(EndpointIds.SchedulesGet, Bundles.Schedules),
        new(EndpointIds.SchedulesList, Bundles.Schedules),
        new(EndpointIds.SchedulesDelete, Bundles.Schedules),

        new(EndpointIds.OptionPresetsCreate, Bundles.Presets),
        new(EndpointIds.OptionPresetsUpdate, Bundles.Presets),
        new(EndpointIds.OptionPresetsGet, Bundles.Presets),
        new(EndpointIds.OptionPresetsList, Bundles.Presets),
        new(EndpointIds.OptionPresetsDelete, Bundles.Presets),
        new(EndpointIds.DownloadConfigSetsCreate, Bundles.DownloadConfigSets),
        new(EndpointIds.DownloadConfigSetsUpdate, Bundles.DownloadConfigSets),
        new(EndpointIds.DownloadConfigSetsGet, Bundles.DownloadConfigSets),
        new(EndpointIds.DownloadConfigSetsList, Bundles.DownloadConfigSets),
        new(EndpointIds.DownloadConfigSetsDelete, Bundles.DownloadConfigSets),

        new(EndpointIds.CreatorSourcesCreate, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesDownloadChannel, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesUpdate, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesGet, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesList, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesRefreshAssets, Bundles.CreatorSources),
        new(EndpointIds.MetadataAccountsRefreshAssets, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesScanNow, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesDelete, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesListIgnoredMedia, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesForceQueueMedia, Bundles.CreatorSources),

        new(EndpointIds.MediaStream, Bundles.Media),
        new(EndpointIds.MediaThumbnail, Bundles.Media),
        new(EndpointIds.MediaCaption, Bundles.Media),
        new(EndpointIds.MediaAccountAsset, Bundles.Media),
        new(EndpointIds.MediaCastToken, Bundles.Media),
        new(EndpointIds.MediaHlsManifest, Bundles.Media),
        new(EndpointIds.MediaHlsSegment, Bundles.Media),
        new(EndpointIds.MediaWatchStateGet, Bundles.Media),
        new(EndpointIds.MediaWatchStateUpsert, Bundles.Media),
        new(EndpointIds.MediaWatchStateMarkWatched, Bundles.Media),
        new(EndpointIds.MediaWatchStateMarkUnwatched, Bundles.Media),
        new(EndpointIds.MediaWatchStateListInProgress, Bundles.Media),
        new(EndpointIds.MediaWatchStateListHistory, Bundles.Media),
        new(EndpointIds.MediaLikeStateGet, Bundles.Media),
        new(EndpointIds.MediaLike, Bundles.Media),
        new(EndpointIds.MediaUnlike, Bundles.Media),
        new(EndpointIds.MediaLikesList, Bundles.Media),
        new(EndpointIds.CastDevicesList, Bundles.Media),
        new(EndpointIds.CastSessionsStart, Bundles.Media),
        new(EndpointIds.CastSessionsList, Bundles.Media),
        new(EndpointIds.CastSessionsGet, Bundles.Media),
        new(EndpointIds.CastSessionsPlay, Bundles.Media),
        new(EndpointIds.CastSessionsPause, Bundles.Media),
        new(EndpointIds.CastSessionsStop, Bundles.Media),
        new(EndpointIds.CastSessionsSeek, Bundles.Media),
        new(EndpointIds.CastSessionsVolume, Bundles.Media),
        new(EndpointIds.CastSessionsDisconnect, Bundles.Media),
        new(EndpointIds.CastSessionsEvents, Bundles.Media),
        new(EndpointIds.PlaylistAudioStream, Bundles.Playlists),

        new(EndpointIds.MediaAccessMediaList, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessMediaAdd, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessMediaRemove, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessMediaClear, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessProviderList, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessProviderAdd, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessProviderRemove, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessProviderClear, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessAgeList, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessAgeAdd, Bundles.MediaAccessAdmin),
        new(EndpointIds.MediaAccessAgeRemove, Bundles.MediaAccessAdmin),

        new(EndpointIds.ManagementCatalog, Bundles.Management),
        new(EndpointIds.ManagementBundlesList, Bundles.Management),
        new(EndpointIds.ManagementBundlesGet, Bundles.Management),
        new(EndpointIds.ManagementBundlesCreate, Bundles.Management),
        new(EndpointIds.ManagementBundlesSetEndpoints, Bundles.Management),
        new(EndpointIds.ManagementBundlesDelete, Bundles.Management),
        new(EndpointIds.ManagementGrantsCreate, Bundles.Management),
        new(EndpointIds.ManagementGrantsDelete, Bundles.Management),

        new(EndpointIds.BackupsCreate, Bundles.Management),
        new(EndpointIds.BackupsJobsList, Bundles.Management),
        new(EndpointIds.BackupsJobsGet, Bundles.Management),
        new(EndpointIds.BackupsList, Bundles.Management),
        new(EndpointIds.BackupsVerify, Bundles.Management),
        new(EndpointIds.BackupsRestorePlan, Bundles.Management),
    ];

    public static readonly IReadOnlySet<string> Ids =
        Endpoints.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Distinct seeded baseline bundle ids (excludes the <c>:all</c> guard bundle).</summary>
    public static readonly IReadOnlySet<string> SeededBundleIds =
        Endpoints.Select(e => e.Bundle).ToHashSet(StringComparer.Ordinal);

    public static bool Contains(string endpointId) => Ids.Contains(endpointId);
}
