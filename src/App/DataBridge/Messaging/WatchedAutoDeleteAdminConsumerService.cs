using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class WatchedAutoDeleteAdminConsumerService(
    IMessageBus messageBus,
    WatchedItemAutoDeleteExecutor executor,
    ILogger<WatchedAutoDeleteAdminConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-watched-auto-delete-admin";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<WatchedAutoDeletePolicyGetRequest>(
            messageBus,
            WatchedAutoDeleteSubjects.GetPolicy,
            HandleGetPolicyAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<WatchedAutoDeletePolicyUpdateRequest>(
            messageBus,
            WatchedAutoDeleteSubjects.UpdatePolicy,
            HandleUpdatePolicyAsync,
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<WatchedAutoDeleteCleanupRunRequest>(
            messageBus,
            WatchedAutoDeleteSubjects.RunCleanup,
            HandleRunCleanupAsync,
            QueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed to watched auto-delete admin subjects.");
    }

    private async Task HandleGetPolicyAsync(IMessageContext<WatchedAutoDeletePolicyGetRequest> context)
    {
        try
        {
            await context.RespondAsync(await executor.GetPolicyAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting watched auto-delete policy.");
            await context.RespondAsync(PolicyFailure("internal", "Failed to get watched auto-delete policy."));
        }
    }

    private async Task HandleUpdatePolicyAsync(IMessageContext<WatchedAutoDeletePolicyUpdateRequest> context)
    {
        try
        {
            await context.RespondAsync(await executor.UpdatePolicyAsync(context.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating watched auto-delete policy.");
            await context.RespondAsync(PolicyFailure("internal", "Failed to update watched auto-delete policy."));
        }
    }

    private async Task HandleRunCleanupAsync(IMessageContext<WatchedAutoDeleteCleanupRunRequest> context)
    {
        try
        {
            await context.RespondAsync(await executor.CleanupAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed running watched auto-delete cleanup.");
            await context.RespondAsync(new WatchedAutoDeleteCleanupResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to run watched auto-delete cleanup."
            });
        }
    }

    private static WatchedAutoDeletePolicyResponse PolicyFailure(string errorCode, string errorMessage)
        => new()
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
}

