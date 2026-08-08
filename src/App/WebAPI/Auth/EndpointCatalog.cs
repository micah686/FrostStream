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
    public const string CreatorMonitor = "creator-monitor";
    public const string Media = "media";
    public const string Notifications = "notifications";
    public const string Management = "management";
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
    public const string DownloadsQueueList = "downloads.queue.list";
    public const string DownloadsQueueGet = "downloads.queue.get";
    public const string DownloadsQueueHistory = "downloads.queue.history";
    public const string DownloadsQueueMedia = "downloads.queue.media";
    public const string DownloadsQueueStream = "downloads.queue.stream";
    public const string DownloadsQueueProgress = "downloads.queue.progress";
    public const string DownloadsQueuePriority = "downloads.queue.priority";
    public const string DownloadsQueueStart = "downloads.queue.start";
    public const string DownloadsQueueStop = "downloads.queue.stop";
    public const string DownloadsQueueCleanup = "downloads.queue.cleanup";
    public const string DownloadsGroupStart = "downloads.group.start";
    public const string DownloadsGroupStop = "downloads.group.stop";
    public const string DownloadsProviderCircuitClear = "downloads.provider-circuit.clear";
    public const string ImportsSessionsCreate = "imports.sessions.create";
    public const string ImportsSessionsGet = "imports.sessions.get";
    public const string ImportsSessionsItemsList = "imports.sessions.items.list";
    public const string ImportsSessionsItemsPatch = "imports.sessions.items.patch";
    public const string ImportsSessionsItemsBulk = "imports.sessions.items.bulk";
    public const string ImportsSessionsMapping = "imports.sessions.mapping";
    public const string ImportsSessionsMappingTemplate = "imports.sessions.mapping-template";
    public const string ImportsSessionsMappingExample = "imports.sessions.mapping-example";
    public const string ImportsSessionsMetadataRefresh = "imports.sessions.metadata.refresh";
    public const string ImportsSessionsEnrich = "imports.sessions.enrich";
    public const string ImportsSessionsUpdateOptions = "imports.sessions.update-options";
    public const string ImportsSessionsCommit = "imports.sessions.commit";
    public const string ImportsSessionsRetry = "imports.sessions.retry";
    public const string ImportsSessionsCancel = "imports.sessions.cancel";
    public const string ImportsSessionsCleanup = "imports.sessions.cleanup";
    public const string ImportsIncomingBrowse = "imports.incoming.browse";
    public const string WorkersList = "workers.list";

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
    public const string MetadataGet = "metadata.get";
    public const string MetadataRandom = "metadata.random";
    public const string MetadataTechnical = "metadata.technical";
    public const string MetadataVersions = "metadata.versions";
    public const string MetadataComments = "metadata.comments";
    public const string MetadataCaptions = "metadata.captions";
    public const string MetadataAccountsList = "metadata.accounts.list";
    public const string MetadataAccountsGet = "metadata.accounts.get";
    public const string MetadataAccountsRefreshAssets = "metadata.accounts.refresh-assets";
    public const string MetadataAccountsGenerateThumbnails = "metadata.accounts.generate-thumbnails";
    public const string MetadataAccountsMedia = "metadata.accounts.media";
    public const string MetadataTaxonomyTags = "metadata.taxonomy.tags";
    public const string MetadataTaxonomyCategories = "metadata.taxonomy.categories";
    public const string MetadataTaxonomyGenres = "metadata.taxonomy.genres";
    public const string StatisticsOverview = "statistics.overview";
    public const string StatisticsChannelsList = "statistics.channels.list";
    public const string StatisticsChannelSuggestions = "statistics.channels.suggestions";
    public const string StatisticsChannelsGet = "statistics.channels.get";
    public const string StatisticsChannelsGetByAccount = "statistics.channels.get-by-account";
    public const string StatisticsDownloadHistory = "statistics.download-history";
    public const string StatisticsCoverageSummary = "statistics.coverage-summary";
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
    public const string MetadataDatabaseReindex = "metadata.database-reindex";
    public const string MediaDelete = "media.delete";
    public const string MediaDeleteForStorageKey = "media.delete-for-key";

    // Playlists
    public const string PlaylistsCreate = "playlists.create";
    public const string PlaylistsList = "playlists.list";
    public const string ProviderPlaylistsLibraryList = "provider-playlists.library.list";
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

    // Background job runs (live, in-memory)
    public const string JobsBackgroundList = "jobs.background.list";
    public const string JobsBackgroundStream = "jobs.background.stream";

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
    public const string CreatorMonitorCreate = "creator-monitor.create";
    public const string CreatorMonitorDownloadChannel = "creator-monitor.download-channel";
    public const string CreatorMonitorUpdate = "creator-monitor.update";
    public const string CreatorMonitorGet = "creator-monitor.get";
    public const string CreatorMonitorList = "creator-monitor.list";
    public const string CreatorMonitorRefreshAssets = "creator-monitor.refresh-assets";
    public const string CreatorMonitorScanNow = "creator-monitor.scan-now";
    public const string CreatorMonitorDelete = "creator-monitor.delete";
    public const string CreatorMonitorListIgnoredMedia = "creator-monitor.list-ignored-media";

    // Media
    public const string MediaStream = "media.stream";
    public const string MediaThumbnail = "media.thumbnail";
    public const string MediaCaption = "media.caption";
    public const string MediaCaptions = "media.captions";
    public const string MediaAccountAsset = "media.account-asset";
    public const string MediaCastToken = "media.cast-token";
    public const string MediaHlsManifest = "media.hls-manifest";
    public const string MediaHlsSegment = "media.hls-segment";
    public const string MediaRenditionsProgressStream = "media.renditions.progress-stream";
    public const string MediaRenditionsQueueList = "media.renditions.queue.list";
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
    public const string ChannelAudioStatus = "channels.audio.status";
    public const string ChannelAudioEncode = "channels.audio.encode";
    public const string ChannelAudioPodcastToken = "channels.audio.podcast-token";
    public const string ChannelAudioPodcastFeed = "channels.audio.podcast-feed";
    public const string ChannelAudioEnclosure = "channels.audio.enclosure";
    public const string ChannelAudioEncodedStatusList = "channels.audio.encoded-status.list";
    public const string ChannelAudioEncodedStatusSet = "channels.audio.encoded-status.set";

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

    // Unified access control. These live in the management baseline bundle and therefore in the
    // `:all` bootstrap bundle. MEDIA_ACCESS.MD defines policies as the only principal-grant surface.
    public const string AccessControlCatalog = "access-control.catalog";
    public const string AccessControlDirectorySearch = "access-control.directory.search";
    public const string AccessControlBundlesList = "access-control.bundles.list";
    public const string AccessControlBundlesGet = "access-control.bundles.get";
    public const string AccessControlBundlePoliciesList = "access-control.bundles.policies.list";
    public const string AccessControlBundlesCreate = "access-control.bundles.create";
    public const string AccessControlBundlesSetEndpoints = "access-control.bundles.set-endpoints";
    public const string AccessControlBundlesDelete = "access-control.bundles.delete";
    public const string AccessControlPoliciesList = "access-control.policies.list";
    public const string AccessControlPoliciesGet = "access-control.policies.get";
    public const string AccessControlPoliciesCreate = "access-control.policies.create";
    public const string AccessControlPoliciesUpdate = "access-control.policies.update";
    public const string AccessControlPoliciesDelete = "access-control.policies.delete";
    public const string AccessControlPoliciesDuplicate = "access-control.policies.duplicate";
    public const string AccessControlProvidersList = "access-control.providers.list";
    public const string AccessControlMediaSummary = "access-control.media.summary";
    public const string AccessControlEffective = "access-control.effective";
    public const string AccessControlEffectiveCheck = "access-control.effective.check";
    public const string AccessControlEffectiveMe = "access-control.effective.me";

    // Backups
    public const string BackupsCreate = "backups.create";
    public const string BackupsJobsList = "backups.jobs.list";
    public const string BackupsJobsGet = "backups.jobs.get";
    public const string BackupsList = "backups.list";
    public const string BackupsVerify = "backups.verify";
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
        new(EndpointIds.DownloadsQueueList, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueGet, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueHistory, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueMedia, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueStream, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueProgress, Bundles.Downloading),
        new(EndpointIds.DownloadsQueuePriority, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueStart, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueStop, Bundles.Downloading),
        new(EndpointIds.DownloadsQueueCleanup, Bundles.Downloading),
        new(EndpointIds.DownloadsGroupStart, Bundles.Downloading),
        new(EndpointIds.DownloadsGroupStop, Bundles.Downloading),
        new(EndpointIds.DownloadsProviderCircuitClear, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCreate, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsGet, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsItemsList, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsItemsPatch, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsItemsBulk, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsMapping, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsMappingTemplate, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsMappingExample, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsMetadataRefresh, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsEnrich, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsUpdateOptions, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCommit, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsRetry, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCancel, Bundles.Downloading),
        new(EndpointIds.ImportsSessionsCleanup, Bundles.Downloading),
        new(EndpointIds.ImportsIncomingBrowse, Bundles.Downloading),
        new(EndpointIds.WorkersList, Bundles.Management),

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
        new(EndpointIds.MetadataGet, Bundles.Metadata),
        new(EndpointIds.MetadataRandom, Bundles.Metadata),
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
        new(EndpointIds.StatisticsChannelSuggestions, Bundles.Metadata),
        new(EndpointIds.StatisticsChannelsGet, Bundles.Metadata),
        new(EndpointIds.StatisticsChannelsGetByAccount, Bundles.Metadata),
        new(EndpointIds.StatisticsDownloadHistory, Bundles.Metadata),
        new(EndpointIds.StatisticsCoverageSummary, Bundles.Metadata),
        new(EndpointIds.UserNotesUpsert, Bundles.Metadata),
        new(EndpointIds.UserNotesGet, Bundles.Metadata),
        new(EndpointIds.UserNotesDelete, Bundles.Metadata),
        new(EndpointIds.UserNotesList, Bundles.Metadata),
        new(EndpointIds.UserNotesSearch, Bundles.Metadata),
        new(EndpointIds.SearchQuery, Bundles.Metadata),
        new(EndpointIds.SearchSimilar, Bundles.Metadata),

        new(EndpointIds.MetadataReindex, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataDatabaseReindex, Bundles.MetadataAdmin),
        new(EndpointIds.MediaDelete, Bundles.MetadataAdmin),
        new(EndpointIds.MediaDeleteForStorageKey, Bundles.MetadataAdmin),

        new(EndpointIds.PlaylistsCreate, Bundles.Playlists),
        new(EndpointIds.PlaylistsList, Bundles.Playlists),
        new(EndpointIds.ProviderPlaylistsLibraryList, Bundles.Playlists),
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

        new(EndpointIds.JobsBackgroundList, Bundles.Schedules),
        new(EndpointIds.JobsBackgroundStream, Bundles.Schedules),

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

        new(EndpointIds.CreatorMonitorCreate, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorDownloadChannel, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorUpdate, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorGet, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorList, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorRefreshAssets, Bundles.CreatorMonitor),
        new(EndpointIds.MetadataAccountsRefreshAssets, Bundles.CreatorMonitor),
        new(EndpointIds.MetadataAccountsGenerateThumbnails, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorScanNow, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorDelete, Bundles.CreatorMonitor),
        new(EndpointIds.CreatorMonitorListIgnoredMedia, Bundles.CreatorMonitor),

        new(EndpointIds.MediaStream, Bundles.Media),
        new(EndpointIds.MediaThumbnail, Bundles.Media),
        new(EndpointIds.MediaCaption, Bundles.Media),
        new(EndpointIds.MediaCaptions, Bundles.Media),
        new(EndpointIds.MediaAccountAsset, Bundles.Media),
        new(EndpointIds.MediaCastToken, Bundles.Media),
        new(EndpointIds.MediaHlsManifest, Bundles.Media),
        new(EndpointIds.MediaHlsSegment, Bundles.Media),
        new(EndpointIds.MediaRenditionsProgressStream, Bundles.Media),
        new(EndpointIds.MediaRenditionsQueueList, Bundles.Media),
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
        new(EndpointIds.ChannelAudioStatus, Bundles.Media),
        new(EndpointIds.ChannelAudioEncode, Bundles.Media),
        new(EndpointIds.ChannelAudioPodcastToken, Bundles.Media),
        new(EndpointIds.ChannelAudioPodcastFeed, Bundles.Media),
        new(EndpointIds.ChannelAudioEnclosure, Bundles.Media),
        new(EndpointIds.ChannelAudioEncodedStatusList, Bundles.Media),
        new(EndpointIds.ChannelAudioEncodedStatusSet, Bundles.Media),

        new(EndpointIds.AccessControlCatalog, Bundles.Management),
        new(EndpointIds.AccessControlDirectorySearch, Bundles.Management),
        new(EndpointIds.AccessControlBundlesList, Bundles.Management),
        new(EndpointIds.AccessControlBundlesGet, Bundles.Management),
        new(EndpointIds.AccessControlBundlePoliciesList, Bundles.Management),
        new(EndpointIds.AccessControlBundlesCreate, Bundles.Management),
        new(EndpointIds.AccessControlBundlesSetEndpoints, Bundles.Management),
        new(EndpointIds.AccessControlBundlesDelete, Bundles.Management),
        new(EndpointIds.AccessControlPoliciesList, Bundles.Management),
        new(EndpointIds.AccessControlPoliciesGet, Bundles.Management),
        new(EndpointIds.AccessControlPoliciesCreate, Bundles.Management),
        new(EndpointIds.AccessControlPoliciesUpdate, Bundles.Management),
        new(EndpointIds.AccessControlPoliciesDelete, Bundles.Management),
        new(EndpointIds.AccessControlPoliciesDuplicate, Bundles.Management),
        new(EndpointIds.AccessControlProvidersList, Bundles.Management),
        new(EndpointIds.AccessControlMediaSummary, Bundles.Management),
        new(EndpointIds.AccessControlEffective, Bundles.Management),
        new(EndpointIds.AccessControlEffectiveCheck, Bundles.Management),
        new(EndpointIds.AccessControlEffectiveMe, Bundles.Management),

        new(EndpointIds.BackupsCreate, Bundles.Management),
        new(EndpointIds.BackupsJobsList, Bundles.Management),
        new(EndpointIds.BackupsJobsGet, Bundles.Management),
        new(EndpointIds.BackupsList, Bundles.Management),
        new(EndpointIds.BackupsVerify, Bundles.Management),
    ];

    public static readonly IReadOnlySet<string> Ids =
        Endpoints.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Distinct seeded baseline bundle ids (excludes the <c>:all</c> guard bundle).</summary>
    public static readonly IReadOnlySet<string> SeededBundleIds =
        Endpoints.Select(e => e.Bundle).ToHashSet(StringComparer.Ordinal);

    public static bool Contains(string endpointId) => Ids.Contains(endpointId);
}
