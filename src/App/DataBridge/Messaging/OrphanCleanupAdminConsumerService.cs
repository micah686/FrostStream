using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class OrphanCleanupAdminConsumerService(
    IMessageBus messageBus,
    OrphanMetadataCleanupExecutor cleanupExecutor,
    IClock clock,
    ILogger<OrphanCleanupAdminConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-orphan-cleanup-admin";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<OrphanCleanupListRequest>(
            messageBus,
            OrphanCleanupSubjects.AdminList,
            HandleListAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<RestoreOrphanRequest>(
            messageBus,
            OrphanCleanupSubjects.AdminRestoreFile,
            HandleRestoreFileAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<RestoreOrphanRequest>(
            messageBus,
            OrphanCleanupSubjects.AdminRestoreMetadata,
            HandleRestoreMetadataAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<OrphanCleanupPolicyGetRequest>(
            messageBus,
            OrphanCleanupSubjects.AdminGetPolicy,
            HandleGetPolicyAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<OrphanCleanupPolicyUpdateRequest>(
            messageBus,
            OrphanCleanupSubjects.AdminUpdatePolicy,
            HandleUpdatePolicyAsync,
            QueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed to orphan cleanup admin requests.");
    }

    private async Task HandleGetPolicyAsync(IMessageContext<OrphanCleanupPolicyGetRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.GetPolicyAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed loading orphan cleanup policy.");
            await context.RespondAsync(InternalPolicyError());
        }
    }

    private async Task HandleUpdatePolicyAsync(IMessageContext<OrphanCleanupPolicyUpdateRequest> context)
    {
        try
        {
            await context.RespondAsync(await cleanupExecutor.UpdatePolicyAsync(
                context.Message,
                clock.GetCurrentInstant()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating orphan cleanup policy.");
            await context.RespondAsync(InternalPolicyError());
        }
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

    private static OrphanCleanupPolicyResponse InternalPolicyError()
        => new()
        {
            Success = false,
            ErrorCode = "internal_error",
            ErrorMessage = "Internal orphan cleanup service error."
        };
}
