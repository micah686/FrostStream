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
    LocalImportItemFlows flows,
    IClock clock,
    ILogger<ImportDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

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
                items = await repo.ListApprovedWorkAsync(session.SessionId, Math.Max(1, session.MaxParallelItems), cancellationToken);
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
                    OperationKey = $"local-import-item/{item.ItemId:N}/attempt/{Math.Max(1, session.ApprovedItems)}",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = 1,
                    SessionId = item.SessionId,
                    ItemId = item.ItemId
                };

                try
                {
                    await flows.Run(item.ItemId.ToString("N"), request);
                }
                catch (InvocationSuspendedException)
                {
                    logger.LogDebug("LocalImportItemFlow suspended after start for ItemId {ItemId}.", item.ItemId);
                }
            }
        }
    }
}
