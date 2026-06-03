using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class OrphanCleanupAdminConsumerService(
    IMessageBus messageBus,
    OrphanMetadataCleanupExecutor cleanupExecutor,
    IClock clock,
    ILogger<OrphanCleanupAdminConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "databridge-orphan-cleanup-admin";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<OrphanCleanupListRequest>(
            OrphanCleanupSubjects.AdminList,
            HandleListAsync,
            QueueGroup,
            stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<RestoreOrphanRequest>(
            OrphanCleanupSubjects.AdminRestoreFile,
            HandleRestoreFileAsync,
            QueueGroup,
            stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<RestoreOrphanRequest>(
            OrphanCleanupSubjects.AdminRestoreMetadata,
            HandleRestoreMetadataAsync,
            QueueGroup,
            stoppingToken));

        logger.LogInformation("Subscribed to orphan cleanup admin requests.");

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

    private async Task HandleListAsync(IMessageContext<OrphanCleanupListRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.ListAsync(context.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing orphan cleanup items.");
            await context.RespondAsync(new OrphanCleanupListResponse
            {
                Success = false,
                ErrorCode = "internal_error",
                ErrorMessage = "Internal orphan cleanup service error."
            });
        }
    }

    private async Task HandleRestoreFileAsync(IMessageContext<RestoreOrphanRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.RestoreFileOrphanAsync(
                context.Message.OrphanId,
                clock.GetCurrentInstant()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed restoring orphan file row {OrphanId}.", context.Message.OrphanId);
            await context.RespondAsync(InternalRestoreError());
        }
    }

    private async Task HandleRestoreMetadataAsync(IMessageContext<RestoreOrphanRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.RestoreMetadataOrphanAsync(
                context.Message.OrphanId,
                clock.GetCurrentInstant()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed restoring orphan metadata row {OrphanId}.", context.Message.OrphanId);
            await context.RespondAsync(InternalRestoreError());
        }
    }

    private static RestoreOrphanResponse InternalRestoreError()
        => new()
        {
            Success = false,
            ErrorCode = "internal_error",
            ErrorMessage = "Internal orphan cleanup service error."
        };
}
