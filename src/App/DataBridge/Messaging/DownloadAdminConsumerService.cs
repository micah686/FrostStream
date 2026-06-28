using DataBridge.Data;
using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Handles NATS Core request/reply operations for download job administration
/// (e.g. changing a queued job's priority).
/// </summary>
public sealed class DownloadAdminConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    DownloadSlotCoordinator slotCoordinator,
    ILogger<DownloadAdminConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-download-admin";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<UpdateDownloadPriorityRequest>(
            messageBus,
            DownloadSubjects.UpdatePriorityRequest,
            HandleUpdatePriorityAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to download-admin subjects.");
    }

    private async Task HandleUpdatePriorityAsync(IMessageContext<UpdateDownloadPriorityRequest> context)
    {
        var req = context.Message;
        try
        {
            DownloadJobState? state;
            string? storageKey;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
                var found = await repo.UpdatePriorityAsync(req.JobId, req.Priority);
                if (!found)
                {
                    await context.RespondAsync(new UpdateDownloadPriorityResponse
                    {
                        Success = false,
                        Error = "Job not found."
                    });
                    return;
                }
                (state, storageKey) = await repo.GetJobStateAndStorageKeyAsync(req.JobId);
            }

            // Tell the coordinator to re-sort only if the job is still waiting for a slot.
            if (state == DownloadJobState.DownloadQueued)
                await slotCoordinator.UpdatePriorityAsync(req.JobId, req.Priority, storageKey);

            logger.LogInformation(
                "Updated priority for JobId {JobId} to {Priority} (state {State}).",
                req.JobId, req.Priority, state);

            await context.RespondAsync(new UpdateDownloadPriorityResponse { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating priority for JobId {JobId}.", req.JobId);
            await context.RespondAsync(new UpdateDownloadPriorityResponse
            {
                Success = false,
                Error = "Internal error."
            });
        }
    }
}
