using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class StorageCreateConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<StorageCreateConsumerService> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<CreateStorageMessage>(
            StorageSubjects.CreateStorage,
            HandleCreateStorageAsync,
            queueGroup: "databridge-storage-create",
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to NATS subject '{Subject}'", StorageSubjects.CreateStorage);

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
        if (_subscription is not null)
        {
            await _subscription.StopAsync(cancellationToken);
            await _subscription.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleCreateStorageAsync(IMessageContext<CreateStorageMessage> context)
    {
        var message = context.Message;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

            var alreadyExists = await dbContext.StorageConfigs
                .AnyAsync(x => x.Key == message.Key);

            if (alreadyExists)
            {
                logger.LogInformation(
                    "Storage key '{StorageKey}' already exists. Skipping NATS create message.",
                    message.Key);
                return;
            }

            var parameterErrors = StorageParametersSerializer.Validate(message.Method, message.Parameters);
            if (parameterErrors.Count > 0)
            {
                logger.LogWarning(
                    "Storage key '{StorageKey}' has invalid parameters payload: {Errors}",
                    message.Key,
                    string.Join("; ", parameterErrors));
                return;
            }

            dbContext.StorageConfigs.Add(new StorageConfigEntity
            {
                Key = message.Key,
                Method = message.Method,
                Parameters = message.Parameters,
                Description = message.Description
            });

            await dbContext.SaveChangesAsync();

            logger.LogInformation(
                "Created storage config '{StorageKey}' from NATS message on '{Subject}'",
                message.Key,
                context.Subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling storage create message for key '{StorageKey}'", message.Key);
        }
    }
}
