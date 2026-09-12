using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Database;
using Shared.Secrets;
using Shared.Storage;

namespace DataBridge.Lite;

/// <summary>Reads storage configuration and hydrates credentials locally, without NATS.</summary>
public sealed class LocalStorageConfigClient(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore) : IStorageConfigClient
{
    public async Task<StorageConfigResponse> GetStorageConfigAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var entity = await db.StorageConfigs
            .AsNoTracking()
            .Include(x => x.Local)
            .Include(x => x.Network)
            .Include(x => x.ObjectS3Compatible)
            .Include(x => x.ObjectAzureBlob)
            .Include(x => x.ObjectGoogleCloudStorage)
            .SingleOrDefaultAsync(x => x.Key == storageKey, cancellationToken);
        var stored = entity?.StoredParameters;
        if (entity is null || stored is null)
            return StorageConfigResponse.NotFound(storageKey);

        var secrets = await secretStore.ReadAsync(SecretPaths.ForStorage(storageKey), cancellationToken)
            .ConfigureAwait(false);
        var hydrated = StorageSecretSplitter.Hydrate(stored, secrets);
        return new StorageConfigResponse(
            true,
            entity.Key,
            entity.Method,
            StorageParametersSerializer.Serialize(entity.Method, hydrated),
            entity.Description,
            entity.WorkerTag);
    }
}
