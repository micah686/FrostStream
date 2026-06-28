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
}

public sealed record DiscoveredMediaUpsertResult(
    int TotalSeen,
    int NewCount,
    int ChangedCount,
    IReadOnlyList<DiscoveredMediaCandidate> EnqueuedItems);
