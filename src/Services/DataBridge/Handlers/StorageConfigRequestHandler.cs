using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace DataBridge.Handlers;

public class StorageConfigRequestHandler : MessageHandlerBase<StorageConfigRequest, StorageConfigResponse>
{
    public StorageConfigRequestHandler(
        FlySwattr.NATS.Abstractions.IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger<StorageConfigRequestHandler> logger)
        : base(messageBus, scopeFactory, logger)
    {
    }

    protected override string Subject => Subjects.StorageConfig;
    protected override string QueueGroup => "databridge-config";

    protected override async Task<StorageConfigResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        StorageConfigRequest request,
        CancellationToken cancellationToken)
    {
        var config = await db.StorageConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == request.StorageKey, cancellationToken);

        if (config is null)
        {
            Logger.LogWarning("Storage config not found for key: {StorageKey}", request.StorageKey);
            return new StorageConfigResponse(
                Found: false,
                Key: null,
                Method: null,
                Parameters: null,
                Description: null);
        }

        Logger.LogDebug("Returning storage config for key: {StorageKey}, method: {Method}", request.StorageKey, config.Method);
        return new StorageConfigResponse(
            Found: true,
            Key: config.Key,
            Method: config.Method,
            Parameters: config.Parameters,
            Description: config.Description);
    }

    protected override StorageConfigResponse CreateErrorResponse(Exception exception)
    {
        return new StorageConfigResponse(
            Found: false,
            Key: null,
            Method: null,
            Parameters: null,
            Description: $"Error: {exception.Message}");
    }
}
