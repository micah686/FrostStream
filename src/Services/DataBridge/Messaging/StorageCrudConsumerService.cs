using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.Messaging;

public sealed class StorageCrudConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<StorageCrudConsumerService> logger) : BackgroundService
{
    private const string DefaultStorageKey = "default";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateLocalRequestMessage>(
            StorageSubjects.CreateLocalStorage,
            HandleCreateLocalStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateStreamingRequestMessage>(
            StorageSubjects.CreateStreamingStorage,
            HandleCreateStreamingStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateObjectRequestMessage>(
            StorageSubjects.CreateObjectStorage,
            HandleCreateObjectStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateLocalRequestMessage>(
            StorageSubjects.UpdateLocalStorage,
            HandleUpdateLocalStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateStreamingRequestMessage>(
            StorageSubjects.UpdateStreamingStorage,
            HandleUpdateStreamingStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateObjectRequestMessage>(
            StorageSubjects.UpdateObjectStorage,
            HandleUpdateObjectStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageListRequestMessage>(
            StorageSubjects.ListStorage,
            HandleListStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageGetRequestMessage>(
            StorageSubjects.GetStorage,
            HandleGetStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageDeleteRequestMessage>(
            StorageSubjects.DeleteStorage,
            HandleDeleteStorageAsync,
            queueGroup: "databridge-storage",
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
        return HandleCreateStorageAsync(context, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleCreateStreamingStorageAsync(IMessageContext<StorageCreateStreamingRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleCreateObjectStorageAsync(IMessageContext<StorageCreateObjectRequestMessage> context)
    {
        return HandleCreateStorageAsync(context, context.Message.Parameters, context.Message.Description);
    }

    private async Task HandleCreateStorageAsync<T>(
        IMessageContext<T> context,
        StorageParametersBase typedParameters,
        string? description)
        where T : class
    {
        var message = context.Message;
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var alreadyExists = await dbContext.StorageConfigs
                .AnyAsync(x => x.Key == GetStorageKey(message));

            if (alreadyExists)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "conflict",
                    ErrorMessage = $"Storage key '{GetStorageKey(message)}' already exists."
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

            var entity = new StorageConfigEntity
            {
                Key = GetStorageKey(message),
                Description = description
            };
            entity.ApplyTypedParameters(typedParameters);

            dbContext.StorageConfigs.Add(entity);
            await dbContext.SaveChangesAsync();

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage create message for key '{StorageKey}'", GetStorageKey(message));
            await RespondInternalErrorAsync(context);
        }
    }

    private Task HandleUpdateLocalStorageAsync(IMessageContext<StorageUpdateLocalRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleUpdateStreamingStorageAsync(IMessageContext<StorageUpdateStreamingRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Parameters, context.Message.Description);
    }

    private Task HandleUpdateObjectStorageAsync(IMessageContext<StorageUpdateObjectRequestMessage> context)
    {
        return HandleUpdateStorageAsync(context, context.Message.Parameters, context.Message.Description);
    }

    private async Task HandleUpdateStorageAsync<T>(
        IMessageContext<T> context,
        StorageParametersBase typedParameters,
        string? description)
        where T : class
    {
        var message = context.Message;
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var entity = await StorageConfigsWithDetails(dbContext)
                .SingleOrDefaultAsync(x => x.Key == GetStorageKey(message));

            if (entity is null)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Storage key '{GetStorageKey(message)}' was not found."
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

            var duplicateKey = await dbContext.StorageConfigs
                .AnyAsync(x => x.Id != entity.Id && x.Key == GetStorageKey(message));

            if (duplicateKey)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "conflict",
                    ErrorMessage = $"Storage key '{GetStorageKey(message)}' already exists."
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

            RemoveExistingParameters(dbContext, entity);

            entity.Key = GetStorageKey(message);
            entity.Description = description;
            entity.LastUpdated = SystemClock.Instance.GetCurrentInstant();
            entity.ApplyTypedParameters(typedParameters);

            await dbContext.SaveChangesAsync();

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage update message for key '{Key}'", GetStorageKey(message));
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

    private static StorageConfigDto Map(StorageConfigEntity entity)
    {
        return new StorageConfigDto
        {
            Id = entity.Id,
            Key = entity.Key,
            Method = entity.Method,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt,
            LastUpdated = entity.LastUpdated,
            Local = entity.Local is null ? null : new PosixLocalStorageParameters
            {
                Protocol = entity.Local.Protocol,
                Path = entity.Local.Path
            },
            Streaming = entity.Network is null ? null : new StreamingNetworkStorageParameters
            {
                Protocol = entity.Network.Protocol,
                Host = entity.Network.Host,
                Port = entity.Network.Port,
                Username = entity.Network.Username,
                Password = entity.Network.Password,
                PrivateKey = entity.Network.PrivateKey,
                PublicKey = entity.Network.PublicKey,
                BasePath = entity.Network.BasePath
            },
            Object = entity.Object is null ? null : new ObjectStorageParameters
            {
                Provider = entity.Object.Provider,
                Container = entity.Object.Container,
                Region = entity.Object.Region,
                Endpoint = entity.Object.Endpoint,
                BasePath = entity.Object.BasePath,
                AccessKeyId = entity.Object.AccessKeyId,
                SecretKey = entity.Object.SecretKey,
                UseDefaultCredentials = entity.Object.UseDefaultCredentials
            }
        };
    }

    private static string GetStorageKey<T>(T message)
        where T : class
    {
        return message switch
        {
            StorageCreateLocalRequestMessage local => local.Key,
            StorageCreateStreamingRequestMessage streaming => streaming.Key,
            StorageCreateObjectRequestMessage @object => @object.Key,
            StorageUpdateLocalRequestMessage local => local.Key,
            StorageUpdateStreamingRequestMessage streaming => streaming.Key,
            StorageUpdateObjectRequestMessage @object => @object.Key,
            StorageGetRequestMessage get => get.Key,
            StorageDeleteRequestMessage delete => delete.Key,
            _ => throw new ArgumentException($"Unsupported storage message type: {typeof(T).Name}", nameof(message))
        };
    }

    private static IQueryable<StorageConfigEntity> StorageConfigsWithDetails(DataBridgeDbContext dbContext)
    {
        return dbContext.StorageConfigs
            .Include(x => x.Local)
            .Include(x => x.Network)
            .Include(x => x.Object);
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

        if (entity.Object is not null)
        {
            dbContext.StorageObjectConfigs.Remove(entity.Object);
            entity.Object = null;
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
