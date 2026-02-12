using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace DataBridge.Handlers;

public class StorageConfigRequestHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StorageConfigRequestHandler> _logger;

    public StorageConfigRequestHandler(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<StorageConfigRequestHandler> logger)
    {
        _messageBus = messageBus;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageConfigRequestHandler subscribing to {Subject}", Subjects.StorageConfig);

        await _messageBus.SubscribeAsync<StorageConfigRequest>(
            Subjects.StorageConfig,
            async context =>
            {
                var storageKey = context.Message.StorageKey;
                _logger.LogDebug("Received storage config request for key: {StorageKey}", storageKey);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                var config = await db.StorageConfigs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == storageKey, stoppingToken);

                if (config is null)
                {
                    _logger.LogWarning("Storage config not found for key: {StorageKey}", storageKey);
                    await context.RespondAsync(new StorageConfigResponse(
                        Found: false,
                        Key: null,
                        Method: null,
                        Parameters: null,
                        Description: null), stoppingToken);
                    return;
                }

                _logger.LogDebug("Returning storage config for key: {StorageKey}, method: {Method}", storageKey, config.Method);
                await context.RespondAsync(new StorageConfigResponse(
                    Found: true,
                    Key: config.Key,
                    Method: config.Method,
                    Parameters: config.Parameters,
                    Description: config.Description), stoppingToken);
            },
            queueGroup: "databridge-config",
            cancellationToken: stoppingToken);
    }
}
