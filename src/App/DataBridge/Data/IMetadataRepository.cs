using Shared.Metadata;

namespace DataBridge.Data;

/// <summary>
/// Writes all <c>metadata.*</c> tables for a given <c>media_guid</c> atomically.
/// The entire write is wrapped in a single PostgreSQL transaction — either all rows
/// commit or none do.
/// </summary>
public interface IMetadataRepository
{
    /// <param name="storageKey">
    /// The storage backend the media's co-located sidecars (thumbnail/captions) live on, recorded
    /// on <c>media_metadata.storage_key</c> and <c>media_captions.storage_key</c> so the filesystem
    /// rescan can scope them to the right backend.
    /// </param>
    Task WriteMetadataAsync(Guid mediaGuid, CapturedMediaMetadata metadata, string storageKey, CancellationToken ct = default);

    /// <summary>
    /// Upserts an account row keyed by <c>(platform, account_handle)</c>, attaching the durable
    /// avatar/banner blob paths and the storage backend they live on. Null blob paths are
    /// preserved (COALESCE), so a refresh that only produced an avatar won't wipe an existing
    /// banner. metadata.accounts is the authoritative table for these assets.
    /// </summary>
    Task UpsertAccountAssetsAsync(
        string platform,
        string accountHandle,
        string accountName,
        string? accountUrl,
        string? avatarStoragePath,
        string? bannerStoragePath,
        string storageKey,
        CancellationToken ct = default);
}
