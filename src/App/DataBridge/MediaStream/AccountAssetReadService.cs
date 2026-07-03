using static DataBridge.NpgsqlDataReaderExtensions;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.MediaStream;

public sealed class AccountAssetReadService(NpgsqlDataSource dataSource) : IAccountAssetReadService
{
    public async Task<AccountAssetLocationDto?> ResolveAsync(
        long accountId,
        AccountAssetType assetType,
        CancellationToken cancellationToken = default)
    {
        var pathColumn = assetType == AccountAssetType.Banner
            ? "banner_storage_path"
            : "avatar_storage_path";

        await using var command = dataSource.CreateCommand($"""
            SELECT
                id,
                storage_key,
                {pathColumn} AS asset_storage_path
            FROM metadata.accounts
            WHERE id = @account_id
              AND storage_key IS NOT NULL
              AND {pathColumn} IS NOT NULL
            """);
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
