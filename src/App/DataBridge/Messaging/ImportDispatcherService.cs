using Cleipnir.ResilientFunctions.Domain.Exceptions;
using DataBridge.Data;
using DataBridge.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class ImportDispatcherService(
    IServiceScopeFactory scopeFactory,
    LocalImportItemV2Flows flows,
    IClock clock,
    ILogger<ImportDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly Duration HashingClaimTimeout = Duration.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import dispatcher poll failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ImportSessionDto> sessions;
        using (var scope = scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
            sessions = await repo.ListCommittingSessionsAsync(20, cancellationToken);
        }

        foreach (var session in sessions)
        {
            IReadOnlyList<ImportSessionItemWork> items;
            using (var scope = scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                var recovered = await repo.RecoverStaleHashingItemsAsync(session.SessionId, clock.GetCurrentInstant() - HashingClaimTimeout, cancellationToken);
                if (recovered > 0)
                    logger.LogWarning(
                        "Recovered {Count} stale local import item(s) stuck in Hashing for SessionId {SessionId}; they will be retried.",
                        recovered,
                        session.SessionId);

                items = await repo.ClaimApprovedWorkAsync(session.SessionId, Math.Max(1, session.MaxParallelItems), cancellationToken);
                if (items.Count == 0)
                {
                    await repo.CompleteSessionIfTerminalAsync(session.SessionId, cancellationToken);
                }
            }

            foreach (var item in items)
            {
                var messageId = Guid.NewGuid();
                var request = new ImportSessionItemImportRequested
                {
                    JobId = item.ItemId,
                    CorrelationId = item.CorrelationId,
                    CausationId = null,
                    MessageId = messageId,
                    OperationKey = LocalImportFlowInstance.OperationKey(item.ItemId, Math.Max(1, item.Attempt), "start"),
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = Math.Max(1, item.Attempt),
                    SessionId = item.SessionId,
                    ItemId = item.ItemId
                };

                try
                {
                    await flows.Run(LocalImportFlowInstance.ForItemAttempt(item.ItemId, request.Attempt), request);
                }
                catch (InvocationSuspendedException)
                {
                    logger.LogDebug("LocalImportItemFlow suspended after start for ItemId {ItemId}.", item.ItemId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed starting local import flow for SessionId {SessionId} ItemId {ItemId}.", item.SessionId, item.ItemId);
                    using var scope = scopeFactory.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
                    await repo.MarkItemCommitFailedAsync(item.SessionId, item.ItemId, "flow_start_failed", ex.Message, ct: cancellationToken);
                    await repo.CompleteSessionIfTerminalAsync(item.SessionId, cancellationToken);
                }
            }
        }
    }
}
