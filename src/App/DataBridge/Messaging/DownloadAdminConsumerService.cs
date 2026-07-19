using Conduit.NATS;
using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class DownloadAdminConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    DownloadJobV2Flows flows,
    DownloadGroupV2Flows groupFlows,
    IClock clock,
    ILogger<DownloadAdminConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-download-v2-admin";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<UpdateDownloadPriorityRequest>(messageBus, DownloadSubjects.UpdatePriorityRequest,
            HandlePriorityAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<StartDownloadRequest>(messageBus, DownloadSubjects.StartDownloadRequest,
            HandleStartAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<StopDownloadRequest>(messageBus, DownloadSubjects.StopDownloadRequest,
            HandleStopAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<StartDownloadGroupRequest>(messageBus, DownloadSubjects.StartGroupRequest,
            HandleStartGroupAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<StopDownloadGroupRequest>(messageBus, DownloadSubjects.StopGroupRequest,
            HandleStopGroupAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<AcquireDownloadLeaseRequest>(messageBus, DownloadSubjects.AcquireLeaseRequest,
            HandleAcquireLeaseAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<RenewDownloadLeaseRequest>(messageBus, DownloadSubjects.RenewLeaseRequest,
            HandleRenewLeaseAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ClearProviderCircuitRequest>(messageBus, DownloadSubjects.ClearProviderCircuitRequest,
            HandleClearProviderAsync, QueueGroup, stoppingToken);
        logger.LogInformation("Subscribed to Download V2 controls.");
    }

    private async Task HandlePriorityAsync(IMessageContext<UpdateDownloadPriorityRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var found = await scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>()
                .UpdatePriorityAsync(context.Message.JobId, context.Message.Priority);
            await context.RespondAsync(new UpdateDownloadPriorityResponse
            {
                Success = found,
                Error = found ? null : "Job not found."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating V2 priority for {JobId}", context.Message.JobId);
            await context.RespondAsync(new UpdateDownloadPriorityResponse { Success = false, Error = "Internal error." });
        }
    }

    private async Task HandleStartAsync(IMessageContext<StartDownloadRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .StartFreshRunAsync(context.Message.JobId);
            if (run is null)
            {
                var rejection = await scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>()
                    .DownloadJobs.AsNoTracking()
                    .Where(x => x.JobId == context.Message.JobId)
                    .Select(x => new { x.FailureCode, x.FailureMessage })
                    .FirstOrDefaultAsync();
                await context.RespondAsync(new StartDownloadResponse
                {
                    Success = false,
                    ErrorCode = rejection?.FailureCode == "provider_circuit_open"
                        ? rejection.FailureCode
                        : "not_restartable",
                    ErrorMessage = rejection?.FailureCode == "provider_circuit_open"
                        ? rejection.FailureMessage
                        : "The job was not found or is not Stopped/Failed."
                });
                return;
            }
            _ = StartFlowAsync(run);
            await context.RespondAsync(new StartDownloadResponse
            {
                Success = true,
                JobId = run.Request.JobId,
                RunId = run.RunId
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed starting V2 JobId {JobId}", context.Message.JobId);
            await context.RespondAsync(new StartDownloadResponse { Success = false, ErrorCode = "internal", ErrorMessage = ex.Message });
        }
    }

    private async Task HandleStopAsync(IMessageContext<StopDownloadRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var decision = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .RequestStopAsync(context.Message.JobId, context.Message.RequestedBy, context.Message.Reason);
            if (decision.Accepted && decision.RunId is { } runId)
                await SignalStopAsync(decision.JobId, runId, context.Message.Reason);
            await context.RespondAsync(new StopDownloadResponse
            {
                Success = decision.Accepted,
                Status = decision.Status,
                ErrorCode = decision.ErrorCode,
                ErrorMessage = decision.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed stopping V2 JobId {JobId}", context.Message.JobId);
            await context.RespondAsync(new StopDownloadResponse { Success = false, ErrorCode = "internal", ErrorMessage = ex.Message });
        }
    }

    private async Task HandleStartGroupAsync(IMessageContext<StartDownloadGroupRequest> context)
    {
        using var scope = scopeFactory.CreateScope();
        var runs = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
            .StartGroupAsync(context.Message.CorrelationId);
        foreach (var run in runs)
            _ = StartFlowAsync(run);
        await context.RespondAsync(new DownloadGroupControlResponse { Success = true, AffectedJobs = runs.Count });
    }

    private async Task HandleStopGroupAsync(IMessageContext<StopDownloadGroupRequest> context)
    {
        using var scope = scopeFactory.CreateScope();
        var decisions = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
            .StopGroupAsync(context.Message.CorrelationId, context.Message.RequestedBy, context.Message.Reason);
        await SignalGroupStopAsync(context.Message.CorrelationId, context.Message.Reason);
        foreach (var decision in decisions.Where(x => x.Accepted && x.RunId is not null))
            await SignalStopAsync(decision.JobId, decision.RunId!.Value, context.Message.Reason);
        await context.RespondAsync(new DownloadGroupControlResponse
        {
            Success = true,
            AffectedJobs = decisions.Count(x => x.Accepted)
        });
    }

    private async Task HandleAcquireLeaseAsync(IMessageContext<AcquireDownloadLeaseRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .TryAcquireLeaseAsync(context.Message);
            await context.RespondAsync(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed acquiring V2 lease for DispatchId {DispatchId}",
                context.Message.Execution.DispatchId);
            // "acquire_error" is deliberately not a fatal rejection code on the Worker: it nacks
            // and retries instead of dropping the dispatch.
            await context.RespondAsync(new AcquireDownloadLeaseResponse
            {
                Granted = false,
                RejectionCode = "acquire_error"
            });
        }
    }

    private async Task HandleRenewLeaseAsync(IMessageContext<RenewDownloadLeaseRequest> context)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .TryRenewLeaseAsync(context.Message);
            await context.RespondAsync(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed renewing V2 lease for DispatchId {DispatchId}",
                context.Message.DispatchId);
            await context.RespondAsync(new RenewDownloadLeaseResponse { Renewed = false });
        }
    }

    private async Task HandleClearProviderAsync(IMessageContext<ClearProviderCircuitRequest> context)
    {
        try
        {
            var provider = context.Message.Provider.Trim().ToLowerInvariant();
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                .ClearProviderCircuitAsync(provider);
            await context.RespondAsync(new ClearProviderCircuitResponse { Success = true });
        }
        catch (Exception ex)
        {
            await context.RespondAsync(new ClearProviderCircuitResponse { Success = false, ErrorMessage = ex.Message });
        }
    }

    private async Task SignalStopAsync(Guid jobId, Guid runId, string? reason)
    {
        var message = new DownloadRunStopRequested
        {
            JobId = jobId,
            RunId = runId,
            MessageId = Guid.NewGuid(),
            Reason = reason,
            OccurredAt = clock.GetCurrentInstant()
        };
        await flows.SendMessage(DownloadFlowInstance.Job(jobId, runId), message,
            idempotencyKey: $"stop/{message.MessageId:N}");
        await messageBus.PublishAsync(DownloadSubjects.StopActiveRun, new StopActiveDownloadRun
        {
            JobId = jobId,
            RunId = runId,
            Reason = reason
        });
    }

    private async Task SignalGroupStopAsync(Guid correlationId, string? reason)
    {
        var message = new DownloadGroupStopRequested
        {
            GroupId = correlationId,
            CorrelationId = correlationId,
            MessageId = Guid.NewGuid(),
            Reason = reason,
            OccurredAt = clock.GetCurrentInstant()
        };
        try
        {
            await groupFlows.SendMessage(
                DownloadFlowInstance.Group(correlationId),
                message,
                idempotencyKey: $"stop/{message.MessageId:N}");
        }
        catch (Exception ex)
        {
            // A completed direct/fan-out group has no live coordinator; child stop decisions
            // above remain authoritative.
            logger.LogDebug(ex, "No active group flow to signal for CorrelationId {CorrelationId}.", correlationId);
        }
    }

    private async Task StartFlowAsync(DownloadRunRequest run)
    {
        try
        {
            await flows.Run(DownloadFlowInstance.Job(run.Request.JobId, run.RunId), run);
        }
        catch (Cleipnir.ResilientFunctions.Domain.Exceptions.InvocationSuspendedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "V2 flow start failed for JobId {JobId} RunId {RunId}", run.Request.JobId, run.RunId);
        }
    }
}

public sealed class DownloadLeaseMonitorService(
    IServiceScopeFactory scopeFactory,
    DownloadJobV2Flows flows,
    ILogger<DownloadLeaseMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
                var expired = await repository.FailExpiredLeasesAsync(stoppingToken);
                foreach (var run in expired.DistinctBy(x => (x.JobId, x.RunId)))
                {
                    var panel = await flows.ControlPanel(new FlowInstance(
                        DownloadFlowInstance.Job(run.JobId, run.RunId)));
                    if (panel is not null)
                        await panel.Delete();
                }
                if (expired.Count > 0)
                    logger.LogWarning(
                        "Failed {Count} Download V2 dispatches after worker lease expiry; their immutable flow instances were deleted.",
                        expired.Count);

                // A terminal flow paired with an active database run leaves the job "running"
                // forever while worker results pile up in a mailbox nobody reads. Convert the
                // inconsistent run into a restartable failure. The one-minute grace keeps the
                // sweep from racing runs whose flow is still being scheduled.
                foreach (var active in await repository.ListActiveRunsAsync(Duration.FromMinutes(1), stoppingToken))
                {
                    var panel = await flows.ControlPanel(new FlowInstance(
                        DownloadFlowInstance.Job(active.JobId, active.RunId)));
                    if (panel is null || panel.Status is not (Status.Failed or Status.Succeeded))
                        continue;
                    var failed = await repository.FailRunAsync(
                        active.JobId, active.RunId, FailureKind.Interrupted, "flow_failed",
                        "The durable download flow terminated before the active run settled. Start the job to create a fresh run.",
                        stoppingToken);
                    await panel.Delete();
                    logger.LogWarning(
                        "Deleted terminal Download V2 flow instance with active run for JobId {JobId} RunId {RunId} FlowStatus {FlowStatus} (run marked failed: {Failed}).",
                        active.JobId, active.RunId, panel.Status, failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download V2 lease sweep failed.");
            }
        }
    }
}
