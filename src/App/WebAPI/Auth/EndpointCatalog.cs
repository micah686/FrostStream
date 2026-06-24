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
    public const string CreatorSources = "creator-sources";
    public const string Media = "media";
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
    public const string DownloadsAudio = "downloads.audio";
    public const string DownloadsPreset = "downloads.preset";

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
    public const string MetadataComments = "metadata.comments";
    public const string MetadataCaptions = "metadata.captions";
    public const string MetadataAccountsList = "metadata.accounts.list";
    public const string MetadataAccountsGet = "metadata.accounts.get";
    public const string MetadataAccountsMedia = "metadata.accounts.media";
    public const string MetadataTaxonomyTags = "metadata.taxonomy.tags";
    public const string MetadataTaxonomyCategories = "metadata.taxonomy.categories";
    public const string MetadataTaxonomyGenres = "metadata.taxonomy.genres";

    // Metadata (admin)
    public const string MetadataReindex = "metadata.reindex";
    public const string MetadataOrphansList = "metadata.orphans.list";
    public const string MetadataOrphansRestoreFile = "metadata.orphans.restore-file";
    public const string MetadataOrphansRestoreMetadata = "metadata.orphans.restore-metadata";

    // Playlists
    public const string PlaylistsCreate = "playlists.create";
    public const string PlaylistsList = "playlists.list";
    public const string PlaylistsGet = "playlists.get";

    // Cookies
    public const string CookiesPut = "cookies.put";
    public const string CookiesList = "cookies.list";
    public const string CookiesGet = "cookies.get";
    public const string CookiesDelete = "cookies.delete";

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

    // Creator sources
    public const string CreatorSourcesCreate = "creator-sources.create";
    public const string CreatorSourcesUpdate = "creator-sources.update";
    public const string CreatorSourcesGet = "creator-sources.get";
    public const string CreatorSourcesList = "creator-sources.list";
    public const string CreatorSourcesRefreshAssets = "creator-sources.refresh-assets";
    public const string CreatorSourcesDelete = "creator-sources.delete";

    // Media
    public const string MediaStream = "media.stream";

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
        new(EndpointIds.MetadataComments, Bundles.Metadata),
        new(EndpointIds.MetadataCaptions, Bundles.Metadata),
        new(EndpointIds.MetadataAccountsList, Bundles.Metadata),
        new(EndpointIds.MetadataAccountsGet, Bundles.Metadata),
        new(EndpointIds.MetadataAccountsMedia, Bundles.Metadata),
        new(EndpointIds.MetadataTaxonomyTags, Bundles.Metadata),
        new(EndpointIds.MetadataTaxonomyCategories, Bundles.Metadata),
        new(EndpointIds.MetadataTaxonomyGenres, Bundles.Metadata),

        new(EndpointIds.MetadataReindex, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataOrphansList, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataOrphansRestoreFile, Bundles.MetadataAdmin),
        new(EndpointIds.MetadataOrphansRestoreMetadata, Bundles.MetadataAdmin),

        new(EndpointIds.PlaylistsCreate, Bundles.Playlists),
        new(EndpointIds.PlaylistsList, Bundles.Playlists),
        new(EndpointIds.PlaylistsGet, Bundles.Playlists),

        new(EndpointIds.CookiesPut, Bundles.Cookies),
        new(EndpointIds.CookiesList, Bundles.Cookies),
        new(EndpointIds.CookiesGet, Bundles.Cookies),
        new(EndpointIds.CookiesDelete, Bundles.Cookies),

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

        new(EndpointIds.CreatorSourcesCreate, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesUpdate, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesGet, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesList, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesRefreshAssets, Bundles.CreatorSources),
        new(EndpointIds.CreatorSourcesDelete, Bundles.CreatorSources),

        new(EndpointIds.MediaStream, Bundles.Media),

        new(EndpointIds.ManagementCatalog, Bundles.Management),
        new(EndpointIds.ManagementBundlesList, Bundles.Management),
        new(EndpointIds.ManagementBundlesGet, Bundles.Management),
        new(EndpointIds.ManagementBundlesCreate, Bundles.Management),
        new(EndpointIds.ManagementBundlesSetEndpoints, Bundles.Management),
        new(EndpointIds.ManagementBundlesDelete, Bundles.Management),
        new(EndpointIds.ManagementGrantsCreate, Bundles.Management),
        new(EndpointIds.ManagementGrantsDelete, Bundles.Management),
    ];

    public static readonly IReadOnlySet<string> Ids =
        Endpoints.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Distinct seeded baseline bundle ids (excludes the <c>:all</c> guard bundle).</summary>
    public static readonly IReadOnlySet<string> SeededBundleIds =
        Endpoints.Select(e => e.Bundle).ToHashSet(StringComparer.Ordinal);

    public static bool Contains(string endpointId) => Ids.Contains(endpointId);
}
