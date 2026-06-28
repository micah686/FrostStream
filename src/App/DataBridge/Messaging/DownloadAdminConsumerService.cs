using DataBridge.Data;
using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
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
    DownloadArchiveFlows flows,
    IClock clock,
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

        await SubscribeAsync<CancelDownloadRequest>(
            messageBus,
            DownloadSubjects.CancelDownloadRequest,
            HandleCancelAsync,
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

    private async Task HandleCancelAsync(IMessageContext<CancelDownloadRequest> context)
    {
        var req = context.Message;
        try
        {
            CancelDownloadDecision decision;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
                decision = await repo.TryBeginCancellationAsync(req.JobId, req.RequestedBy, req.Reason);
            }

            if (!decision.Accepted)
            {
                await context.RespondAsync(new CancelDownloadResponse
                {
                    Success = false,
                    State = decision.State,
                    Error = decision.Error
                });
                return;
            }

            if (decision.PreviousState is DownloadJobState.DownloadQueued or DownloadJobState.Cancelling)
            {
                await slotCoordinator.CancelQueuedAsync(req.JobId, decision.WorkerTag);
                await SendCancelToFlowAsync(req, decision.CorrelationId!.Value);
            }

            if (decision.PreviousState is DownloadJobState.DownloadPending or DownloadJobState.Cancelling)
            {
                await messageBus.PublishAsync(
                    DownloadSubjects.CancelActiveDownloadCommand,
                    new CancelActiveDownloadCommand
                    {
                        JobId = req.JobId,
                        MessageId = Guid.NewGuid(),
                        RequestedBy = req.RequestedBy,
                        Reason = req.Reason
                    });
            }

            logger.LogInformation(
                "Accepted cancellation for JobId {JobId}; previous state {PreviousState}, worker tag {WorkerTag}.",
                req.JobId,
                decision.PreviousState,
                decision.WorkerTag);

            await context.RespondAsync(new CancelDownloadResponse
            {
                Success = true,
                State = decision.State
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed cancelling JobId {JobId}.", req.JobId);
            await context.RespondAsync(new CancelDownloadResponse
            {
                Success = false,
                Error = "Internal error."
            });
        }
    }

    private async Task SendCancelToFlowAsync(CancelDownloadRequest req, Guid correlationId)
    {
        var messageId = Guid.NewGuid();
        await flows.SendMessage(req.JobId.ToString("N"), new DownloadCancelRequested
        {
            JobId = req.JobId,
            CorrelationId = correlationId,
            CausationId = null,
            MessageId = messageId,
            OperationKey = $"job/{req.JobId:N}/cancel/{messageId:N}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            RequestedBy = req.RequestedBy,
            Reason = req.Reason
        }, idempotencyKey: $"job/{req.JobId:N}/cancel");
    }
}
