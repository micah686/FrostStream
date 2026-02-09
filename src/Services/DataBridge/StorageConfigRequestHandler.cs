using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace DataBridge;

/// <summary>
/// Background service that handles storage configuration requests from other services.
/// Listens on <see cref="Subjects.StorageConfig"/> and responds with configuration from the database.
/// </summary>
public class StorageConfigRequestHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StorageConfigRequestHandler> _logger;

    // Queue group for load balancing across DataBridge instances
    private const string QueueGroup = "databridge-config";

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
        _logger.LogInformation("Starting storage config request handler on subject '{Subject}' with queue group '{QueueGroup}'",
            Subjects.StorageConfig, QueueGroup);

        await _messageBus.SubscribeAsync<StorageConfigRequest>(
            Subjects.StorageConfig,
            HandleRequestAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);
    }

    private async Task HandleRequestAsync(IMessageContext<StorageConfigRequest> ctx)
    {
        var storageKey = ctx.Message.StorageKey;
        _logger.LogDebug("Received storage config request for key '{StorageKey}'", storageKey);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

            var config = await dbContext.StorageConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == storageKey);

            if (config is null)
            {
                _logger.LogWarning("Storage config not found for key '{StorageKey}'", storageKey);
                // Respond with empty/null to indicate not found
                // Note: NatsMessageBus handles null responses appropriately
                return;
            }

            var response = new StorageConfigResponse
            {
                Method = config.Method,
                ConnectionString = config.ConnectionString,
                RemotePath = config.RemotePath
            };

            _logger.LogDebug("Responding with storage config for key '{StorageKey}': Method={Method}",
                storageKey, response.Method);

            await ctx.RespondAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling storage config request for key '{StorageKey}'", storageKey);
            throw;
        }
    }
}
