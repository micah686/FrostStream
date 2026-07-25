using Microsoft.Extensions.DependencyInjection;

namespace Shared.Storage;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the storage config client and a cached blob-storage provider
    /// that hydrates secrets from the configured <c>ISecretStore</c>. Also
    /// registers the cache-invalidation subscriber so <c>StorageConfigChanged</c>
    /// events evict stale entries.
    /// </summary>
    public static IServiceCollection AddFrostStreamStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStorageConfigClient, NatsStorageConfigClient>();
        services.AddSingleton<IStoreProvider, CachingStoreProvider>();
        services.AddHostedService<StorageConfigChangedSubscriber>();
        return services;
    }
}
