using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Data;

public interface ICreatorDiscoveryRepository
{
    Task<CreatorSourceEntity?> GetSourceAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CreatorSourceEntity>> ListSourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CreatorSourceEntity>> ListEnabledSourcesForScanAsync(CreatorSourceScanMode scanMode, CancellationToken cancellationToken = default);
    Task<CreatorSourceEntity> CreateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default);
    Task<CreatorSourceEntity> CreateOrReuseSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default);
    Task<CreatorSourceEntity?> UpdateSourceAsync(CreatorSourceEntity source, CancellationToken cancellationToken = default);
    Task<bool> DeleteSourceAsync(long id, CancellationToken cancellationToken = default);
    Task<DiscoveredMediaUpsertResult> UpsertDiscoveredMediaBatchAsync(UpsertDiscoveredMediaBatchRequestMessage request, CancellationToken cancellationToken = default);
    Task<CreatorSourceEntity?> UpdateAssetsAsync(UpdateCreatorSourceAssetsRequestMessage request, CancellationToken cancellationToken = default);

    /// <summary>Lists discovered-media rows for a source that were suppressed by an ignore keyword.</summary>
    Task<IReadOnlyList<DiscoveredMediaEntity>> ListIgnoredMediaAsync(long creatorSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the ignored state of a discovered-media row (status back to <see cref="MediaDiscoveryStatus.Queued"/>,
    /// keyword cleared) and returns it so the caller can publish a forced download. Returns null when not found.
    /// </summary>
    Task<DiscoveredMediaEntity?> RequeueIgnoredMediaAsync(long discoveredMediaId, CancellationToken cancellationToken = default);
}

public sealed record DiscoveredMediaUpsertResult(
    int TotalSeen,
    int NewCount,
    int ChangedCount,
    IReadOnlyList<DiscoveredMediaCandidate> EnqueuedItems);
