using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;

namespace DataBridge.Messaging;

public sealed class StorageCrudConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    ILogger<StorageCrudConsumerService> logger) : BackgroundService
{
    private const string DefaultStorageKey = "default";
    private const string QueueGroup = "databridge-storage";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateLocalRequestMessage>(
            StorageSubjects.CreateLocalStorage,
            HandleCreateLocalStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateStreamingRequestMessage>(
            StorageSubjects.CreateNetworkStorage,
            HandleCreateStreamingStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateS3CompatibleObjectRequestMessage>(
            StorageSubjects.CreateS3CompatibleObjectStorage,
            HandleCreateS3CompatibleObjectStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateAzureBlobObjectRequestMessage>(
            StorageSubjects.CreateAzureBlobObjectStorage,
            HandleCreateAzureBlobObjectStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateGoogleCloudStorageObjectRequestMessage>(
            StorageSubjects.CreateGoogleCloudStorageObjectStorage,
            HandleCreateGoogleCloudStorageObjectStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateLocalRequestMessage>(
            StorageSubjects.UpdateLocalStorage,
            HandleUpdateLocalStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateStreamingRequestMessage>(
            StorageSubjects.UpdateNetworkStorage,
            HandleUpdateStreamingStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateS3CompatibleObjectRequestMessage>(
            StorageSubjects.UpdateS3CompatibleObjectStorage,
            HandleUpdateS3CompatibleObjectStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateAzureBlobObjectRequestMessage>(
            StorageSubjects.UpdateAzureBlobObjectStorage,
            HandleUpdateAzureBlobObjectStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateGoogleCloudStorageObjectRequestMessage>(
            StorageSubjects.UpdateGoogleCloudStorageObjectStorage,
            HandleUpdateGoogleCloudStorageObjectStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageListRequestMessage>(
            StorageSubjects.ListStorage,
            HandleListStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            HandleGetStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageDeleteRequestMessage>(
            StorageSubjects.DeleteStorage,
            HandleDeleteStorageAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        logger.LogInformation("Subscribed to storage CRUD subjects.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private Task HandleCreateLocalStorageAsync(IMessageContext<StorageCreateLocalRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleCreateStreamingStorageAsync(IMessageContext<StorageCreateStreamingRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleCreateS3CompatibleObjectStorageAsync(IMessageContext<StorageCreateS3CompatibleObjectRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleCreateAzureBlobObjectStorageAsync(IMessageContext<StorageCreateAzureBlobObjectRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleCreateGoogleCloudStorageObjectStorageAsync(IMessageContext<StorageCreateGoogleCloudStorageObjectRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private async Task HandleCreateStorageAsync<T>(
        IMessageContext<T> context,
        string storageKey,
        StorageParametersBase typedParameters,
        string? description)
        where T : class
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var alreadyExists = await dbContext.StorageConfigs
                .AnyAsync(x => x.Key == storageKey);

            if (alreadyExists)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "conflict",
                    ErrorMessage = $"Storage key '{storageKey}' already exists."
                });
                return;
            }

            var parameterErrors = StorageParametersSerializer.Validate(typedParameters);
            if (parameterErrors.Count > 0)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = string.Join("; ", parameterErrors)
                });
                return;
            }

            var (secrets, stored) = StorageSecretSplitter.Split(typedParameters);

            // Vault-write before DB-write so "DB row exists ⇒ Vault path exists" holds.
            // Best-effort cleanup if DB write fails afterward.
            if (secrets.Count > 0)
            {
                await secretStore.WriteAsync(SecretPaths.ForStorage(storageKey), secrets);
            }

            var entity = new StorageConfigEntity
            {
                Key = storageKey,
                Description = description
            };
            entity.ApplyStoredParameters(stored);

            try
            {
                dbContext.StorageConfigs.Add(entity);
                await dbContext.SaveChangesAsync();
            }
            catch
            {
                if (secrets.Count > 0)
                {
                    await SafeDeleteSecretAsync(storageKey);
                }
                throw;
            }

            await PublishChangedAsync(storageKey, StorageConfigChangeKind.Created);

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage create message for key '{StorageKey}'", storageKey);
            await RespondInternalErrorAsync(context);
        }
    }

    private Task HandleUpdateLocalStorageAsync(IMessageContext<StorageUpdateLocalRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleUpdateStreamingStorageAsync(IMessageContext<StorageUpdateStreamingRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleUpdateS3CompatibleObjectStorageAsync(IMessageContext<StorageUpdateS3CompatibleObjectRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleUpdateAzureBlobObjectStorageAsync(IMessageContext<StorageUpdateAzureBlobObjectRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleUpdateGoogleCloudStorageObjectStorageAsync(IMessageContext<StorageUpdateGoogleCloudStorageObjectRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Key, context.Message.Parameters, context.Message.Description);
    }

    private async Task HandleUpdateStorageAsync<T>(
        IMessageContext<T> context,
        string storageKey,
        StorageParametersBase typedParameters,
        string? description)
        where T : class
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var entity = await StorageConfigsWithDetails(dbContext)
                .SingleOrDefaultAsync(x => x.Key == storageKey);

            if (entity is null)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Storage key '{storageKey}' was not found."
                });
                return;
            }

            if (entity.Key == DefaultStorageKey)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "forbidden",
                    ErrorMessage = $"Storage key '{DefaultStorageKey}' is immutable and cannot be updated."
                });
                return;
            }

            var parameterErrors = StorageParametersSerializer.Validate(typedParameters);
            if (parameterErrors.Count > 0)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "validation",
                    ErrorMessage = string.Join("; ", parameterErrors)
                });
                return;
            }

            var (secrets, stored) = StorageSecretSplitter.Split(typedParameters);

            // Update Vault first so any reader that arrives mid-update sees a coherent state.
            if (secrets.Count > 0)
            {
                await secretStore.WriteAsync(SecretPaths.ForStorage(storageKey), secrets);
            }
            else
            {
                // Nothing sensitive supplied this round — clear the existing secret bundle so old
                // values don't linger after a switch (e.g. password → key-based auth).
                await SafeDeleteSecretAsync(storageKey);
            }

            RemoveExistingParameters(dbContext, entity);

            entity.Key = storageKey;
            entity.Description = description;
            entity.LastUpdated = SystemClock.Instance.GetCurrentInstant();
            entity.ApplyStoredParameters(stored);

            await dbContext.SaveChangesAsync();

            await PublishChangedAsync(storageKey, StorageConfigChangeKind.Updated);

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage update message for key '{Key}'", storageKey);
            await RespondInternalErrorAsync(context);
        }
    }

    private async Task HandleListStorageAsync(IMessageContext<StorageListRequestMessage> context)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var items = await StorageConfigsWithDetails(dbContext)
                .OrderBy(x => x.Id)
                .ToArrayAsync();

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Items = items.Select(Map).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage list message.");
            await RespondInternalErrorAsync(context);
        }
    }

    private async Task HandleGetStorageAsync(IMessageContext<StorageGetRequestMessage> context)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var entity = await StorageConfigsWithDetails(dbContext)
                .SingleOrDefaultAsync(x => x.Key == context.Message.Key);

            if (entity is null)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Storage key '{context.Message.Key}' was not found."
                });
                return;
            }

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage get message for key '{StorageKey}'", context.Message.Key);
            await RespondInternalErrorAsync(context);
        }
    }

    private async Task HandleDeleteStorageAsync(IMessageContext<StorageDeleteRequestMessage> context)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var entity = await StorageConfigsWithDetails(dbContext)
                .SingleOrDefaultAsync(x => x.Key == context.Message.Key);

            if (entity is null)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Storage key '{context.Message.Key}' was not found."
                });
                return;
            }

            if (entity.Key == DefaultStorageKey)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "forbidden",
                    ErrorMessage = $"Storage key '{DefaultStorageKey}' is immutable and cannot be deleted."
                });
                return;
            }

            dbContext.StorageConfigs.Remove(entity);
            await dbContext.SaveChangesAsync();

            // DB row is the source of truth; orphan secrets in Vault are tolerable but log on failure.
            await SafeDeleteSecretAsync(context.Message.Key);

            await PublishChangedAsync(context.Message.Key, StorageConfigChangeKind.Deleted);

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage delete message for key '{StorageKey}'", context.Message.Key);
            await RespondInternalErrorAsync(context);
        }
    }

    private async Task SafeDeleteSecretAsync(string storageKey)
    {
        try
        {
            await secretStore.DeleteAsync(SecretPaths.ForStorage(storageKey));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed deleting secrets at path for storage key '{StorageKey}'; orphan may remain.", storageKey);
        }
    }

    private async Task PublishChangedAsync(string storageKey, StorageConfigChangeKind change)
    {
        try
        {
            await messageBus.PublishAsync(
                StorageSubjects.StorageConfigChanged,
                new StorageConfigChangedMessage { Key = storageKey, Change = change });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed publishing StorageConfigChanged for key '{StorageKey}'", storageKey);
        }
    }

    private static StorageConfigDto Map(StorageConfigEntity entity)
    {
        var stored = entity.StoredParameters;
        return new StorageConfigDto
        {
            Id = entity.Id,
            Key = entity.Key,
            Method = entity.Method,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt,
            LastUpdated = entity.LastUpdated,
            Local = stored as PosixLocalStorageStored,
            Network = stored as StreamingNetworkStorageStored,
            ObjectS3Compatible = stored as S3CompatibleObjectStorageStored,
            ObjectAzureBlob = stored as AzureBlobObjectStorageStored,
            ObjectGoogleCloudStorage = stored as GoogleCloudStorageObjectStorageStored
        };
    }

    private static IQueryable<StorageConfigEntity> StorageConfigsWithDetails(DataBridgeDbContext dbContext)
    {
        return dbContext.StorageConfigs
            .Include(x => x.Local)
            .Include(x => x.Network)
            .Include(x => x.ObjectS3Compatible)
            .Include(x => x.ObjectAzureBlob)
            .Include(x => x.ObjectGoogleCloudStorage);
    }

    private static void RemoveExistingParameters(DataBridgeDbContext dbContext, StorageConfigEntity entity)
    {
        if (entity.Local is not null)
        {
            dbContext.StorageLocalConfigs.Remove(entity.Local);
            entity.Local = null;
        }

        if (entity.Network is not null)
        {
            dbContext.StorageNetworkConfigs.Remove(entity.Network);
            entity.Network = null;
        }

        if (entity.ObjectS3Compatible is not null)
        {
            dbContext.StorageS3CompatibleObjectConfigs.Remove(entity.ObjectS3Compatible);
            entity.ObjectS3Compatible = null;
        }

        if (entity.ObjectAzureBlob is not null)
        {
            dbContext.StorageAzureBlobObjectConfigs.Remove(entity.ObjectAzureBlob);
            entity.ObjectAzureBlob = null;
        }

        if (entity.ObjectGoogleCloudStorage is not null)
        {
            dbContext.StorageGoogleCloudStorageObjectConfigs.Remove(entity.ObjectGoogleCloudStorage);
            entity.ObjectGoogleCloudStorage = null;
        }
    }

    private static Task RespondInternalErrorAsync<T>(IMessageContext<T> context)
    {
        return context.RespondAsync(new StorageOperationResponseMessage
        {
            Success = false,
            ErrorCode = "internal_error",
            ErrorMessage = "Internal storage service error."
        });
    }
}
