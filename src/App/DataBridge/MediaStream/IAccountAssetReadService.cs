using Shared.Messaging;

namespace DataBridge.MediaStream;

public interface IAccountAssetReadService
{
    Task<AccountAssetLocationDto?> ResolveAsync(
        long accountId,
        AccountAssetType assetType,
        CancellationToken cancellationToken = default);
}
