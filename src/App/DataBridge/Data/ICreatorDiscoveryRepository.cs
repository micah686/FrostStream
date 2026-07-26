using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public interface ICreatorDiscoveryRepository
{
    Task<CreatorSourceRecord?> GetSourceAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CreatorSourceRecord>> ListSourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CreatorSourceRecord>> ListEnabledSourcesForScanAsync(CreatorSourceScanMode scanMode, CancellationToken cancellationToken = default);
    Task<CreatorSourceRecord> CreateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default);
    Task<CreatorSourceRecord> CreateOrReuseSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default);
    Task<CreatorSourceRecord?> UpdateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default);
    Task<bool> DeleteSourceAsync(long id, CancellationToken cancellationToken = default);
    Task<DiscoveredMediaUpsertResult> UpsertDiscoveredMediaBatchAsync(UpsertDiscoveredMediaBatchRequestMessage request, CancellationToken cancellationToken = default);
    Task<CreatorSourceRecord?> UpdateAssetsAsync(UpdateCreatorMonitorAssetsRequestMessage request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Points a source at the metadata.accounts row it belongs to. Called after an asset refresh
    /// resolves the account, so the association stops depending on (platform, handle) string matching.
    /// </summary>
    Task LinkAccountAsync(long creatorSourceId, long accountId, CancellationToken cancellationToken = default);

    /// <summary>Lists discovered-media rows for a source that were suppressed by an ignore keyword.</summary>
    Task<IReadOnlyList<DiscoveredMediaEntity>> ListIgnoredMediaAsync(long creatorSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the ignored state of a discovered-media row (status back to <see cref="MediaDiscoveryStatus.Queued"/>,
    /// keyword cleared) and returns it so the caller can publish a forced download. Returns null when not found.
    /// </summary>
    Task<DiscoveredMediaEntity?> RequeueIgnoredMediaAsync(long discoveredMediaId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A creator source paired with its background-job state. <see cref="ScanState"/> is only null for a
/// source whose companion row is missing, which the repository heals on the next write.
/// </summary>
public sealed record CreatorSourceRecord(CreatorSourceEntity Source, CreatorScanStateEntity? ScanState);

public sealed record DiscoveredMediaUpsertResult(
    int TotalSeen,
    int NewCount,
    int ChangedCount,
    IReadOnlyList<DiscoveredMediaCandidate> EnqueuedItems);
