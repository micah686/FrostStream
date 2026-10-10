using static DataBridge.ApplicationDataReaderExtensions;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class AccountAssetReadService(ApplicationDatabase dataSource) : IAccountAssetReadService
{
    public async Task<AccountAssetLocationDto?> ResolveAsync(
        long accountId,
        AccountAssetType assetType,
        CancellationToken cancellationToken = default)
    {
        var pathColumn = assetType == AccountAssetType.Banner
            ? "banner_storage_path"
            : "avatar_storage_path";

        await using var command = dataSource.CreateCommand(dataSource.Sql("AccountAssetReadService.ResolveAsync.1", pathColumn, pathColumn));
        command.Parameters.AddWithValue("@account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AccountAssetLocationDto
        {
            AccountId = GetInt64(reader, "id"),
            StorageKey = GetString(reader, "storage_key"),
            StoragePath = GetString(reader, "asset_storage_path")
        };
    }
}
