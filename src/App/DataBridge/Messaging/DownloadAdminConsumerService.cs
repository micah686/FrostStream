using DataBridge.Data;
using DataBridge.Flows;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Domain.Exceptions;
using Conduit.NATS;
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

        await SubscribeAsync<RestartHaltedDownloadRequest>(
            messageBus,
            DownloadSubjects.RestartHaltedDownloadRequest,
            HandleRestartHaltedAsync,
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

    private async Task HandleRestartHaltedAsync(IMessageContext<RestartHaltedDownloadRequest> context)
    {
        var req = context.Message;
        try
        {
            DownloadRequested? originalRequest;
            DownloadJobState restartFromState;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
                var job = await repo.GetJobStateAndStorageKeyAsync(req.JobId);
                if (job.State is null)
                {
                    await context.RespondAsync(new RestartHaltedDownloadResponse
                    {
                        Success = false,
                        ErrorCode = "not_found",
                        ErrorMessage = $"Job '{req.JobId}' was not found."
                    });
                    return;
                }

                if (job.State is not (DownloadJobState.ProviderHalted or DownloadJobState.Cancelled))
                {
                    await context.RespondAsync(new RestartHaltedDownloadResponse
                    {
                        Success = false,
                        ErrorCode = "not_halted",
                        ErrorMessage = $"Job '{req.JobId}' cannot be restarted from state {job.State}."
                    });
                    return;
                }
                restartFromState = job.State.Value;

                originalRequest = await repo.GetOriginalRequestAsync(req.JobId);
                if (originalRequest is null)
                {
                    await context.RespondAsync(new RestartHaltedDownloadResponse
                    {
                        Success = false,
                        ErrorCode = "missing_request",
                        ErrorMessage = $"Original request for job '{req.JobId}' was not found."
                    });
                    return;
                }
            }

            var replay = originalRequest with
            {
                MessageId = Guid.NewGuid(),
                CausationId = originalRequest.MessageId,
                OperationKey = $"job/{req.JobId:N}/restart/{Guid.NewGuid():N}",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = originalRequest.Attempt + 1,
                ResumeFromHaltedState = restartFromState is DownloadJobState.ProviderHalted
            };

            try
            {
                var controlPanel = (await flows.ControlPanel(new FlowInstance(req.JobId.ToString("N"))))!;
                if (restartFromState is DownloadJobState.Cancelled)
                    await ClearReplayStateAsync(controlPanel);
                controlPanel.Param = replay;
                await controlPanel.Restart(clearFailures: restartFromState is DownloadJobState.ProviderHalted, refresh: false);
            }
            catch (InvocationSuspendedException)
            {
                logger.LogWarning(
                    "DownloadArchiveFlow suspended after restart for JobId {JobId}; treating restart as accepted.",
                    req.JobId);
            }

            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
                await repo.RecordHistoryAsync(
                    replay.JobId,
                    replay.MessageId,
                    replay.OperationKey,
                    nameof(DownloadRequested),
                    System.Text.Json.JsonSerializer.Serialize(replay));
            }

            logger.LogInformation("Restarted download JobId {JobId} from state {State}.", req.JobId, restartFromState);

            await context.RespondAsync(new RestartHaltedDownloadResponse
            {
                Success = true,
                JobId = req.JobId
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed restarting halted download JobId {JobId}.", req.JobId);

            await context.RespondAsync(new RestartHaltedDownloadResponse
            {
                Success = false,
                ErrorCode = "internal",
                ErrorMessage = "Failed to restart halted download."
            });
        }
    }

    private static async Task ClearReplayStateAsync(Cleipnir.ResilientFunctions.Domain.ControlPanel<DownloadRequested> controlPanel)
    {
        var effectIds = (await controlPanel.Effects.AllIds).ToList();
        foreach (var effectId in effectIds)
            await controlPanel.Effects.Remove(effectId);

        await controlPanel.Messages.Clear();
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

            if (decision.PreviousState is DownloadJobState.DownloadQueued or DownloadJobState.DownloadPending
                or DownloadJobState.Cancelling or DownloadJobState.FailedTransient)
            {
                // For FailedTransient there's no active flow instance to signal (see
                // TryBeginCancellationAsync) — this publish is purely a best-effort kill signal for
                // the known race where a yt-dlp process can still be running on the worker even
                // though the saga already recorded a terminal failure. The Worker no-ops harmlessly
                // if it finds no active download for this JobId.
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
