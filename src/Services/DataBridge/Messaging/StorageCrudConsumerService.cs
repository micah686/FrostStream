using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared;
using Shared.Messaging;

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
        _subscriptions.Add(await messageBus.SubscribeAsync<StorageCreateRequestMessage>(
            StorageSubjects.CreateStorage,
            HandleCreateStorageAsync,
            queueGroup: "databridge-storage",
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<StorageUpdateRequestMessage>(
            StorageSubjects.UpdateStorage,
            HandleUpdateStorageAsync,
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

    private async Task HandleCreateStorageAsync(IMessageContext<StorageCreateRequestMessage> context)
    {
        var message = context.Message;
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var alreadyExists = await dbContext.StorageConfigs
                .AnyAsync(x => x.Key == message.Key);

            if (alreadyExists)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "conflict",
                    ErrorMessage = $"Storage key '{message.Key}' already exists."
                });
                return;
            }

            var parameterErrors = StorageParametersSerializer.Validate(message.Method, message.Parameters);
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
                Key = message.Key,
                Method = message.Method,
                Parameters = message.Parameters,
                Description = message.Description
            };

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
            logger.LogError(ex, "Failed handling storage create message for key '{StorageKey}'", message.Key);
            await RespondInternalErrorAsync(context);
        }
    }

    private async Task HandleUpdateStorageAsync(IMessageContext<StorageUpdateRequestMessage> context)
    {
        var message = context.Message;
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var entity = await dbContext.StorageConfigs
                .SingleOrDefaultAsync(x => x.Key == message.Key);

            if (entity is null)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "not_found",
                    ErrorMessage = $"Storage key '{message.Key}' was not found."
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
                .AnyAsync(x => x.Id != entity.Id && x.Key == message.Key);

            if (duplicateKey)
            {
                await context.RespondAsync(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = "conflict",
                    ErrorMessage = $"Storage key '{message.Key}' already exists."
                });
                return;
            }

            var parameterErrors = StorageParametersSerializer.Validate(message.Method, message.Parameters);
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

            entity.Key = message.Key;
            entity.Method = message.Method;
            entity.Parameters = message.Parameters;
            entity.Description = message.Description;
            entity.LastUpdated = SystemClock.Instance.GetCurrentInstant();

            await dbContext.SaveChangesAsync();

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage update message for key '{Key}'", message.Key);
            await RespondInternalErrorAsync(context);
        }
    }

    private async Task HandleListStorageAsync(IMessageContext<StorageListRequestMessage> context)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        try
        {
            var items = await dbContext.StorageConfigs
                .OrderBy(x => x.Id)
                .Select(x => new StorageConfigDto
                {
                    Id = x.Id,
                    Key = x.Key,
                    Method = x.Method,
                    Parameters = x.Parameters,
                    Description = x.Description,
                    CreatedAt = x.CreatedAt,
                    LastUpdated = x.LastUpdated
                })
                .ToArrayAsync();

            await context.RespondAsync(new StorageOperationResponseMessage
            {
                Success = true,
                Items = items
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
            var entity = await dbContext.StorageConfigs
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
            var entity = await dbContext.StorageConfigs
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
            Parameters = entity.Parameters,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt,
            LastUpdated = entity.LastUpdated
        };
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
