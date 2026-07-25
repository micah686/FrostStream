using Conduit.NATS;
using Shared.Messaging;

namespace WebAPI.Auth;

/// <summary>
/// Retries policies whose database revision has not yet been reflected in OpenFGA. Media denies are
/// database-native and apply independently; synchronization status governs endpoint grants only.
/// </summary>
public sealed class AccessPolicyReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<AccessPolicyReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await ReconcileAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var openFga = scope.ServiceProvider.GetRequiredService<IAccessPolicyOpenFgaService>();

        AccessPolicyOperationResponseMessage? response;
        try
        {
            response = await bus.RequestAsync<AccessPolicyListRequestMessage, AccessPolicyOperationResponseMessage>(
                AccessPolicySubjects.List,
                new AccessPolicyListRequestMessage(),
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Access-policy reconciliation could not list policies.");
            return;
        }

        if (response is null || !response.Success)
        {
            logger.LogDebug("Access-policy reconciliation list failed: {Error}", response?.ErrorMessage);
            return;
        }

        foreach (var policy in (response.Policies ?? [])
                     .Where(x => x.SyncStatus is AccessPolicySyncStatus.Pending or AccessPolicySyncStatus.Failed))
        {
            var result = await openFga.SynchronizeAsync(policy, cancellationToken);
            var status = result.Status == BundleOpStatus.Ok
                ? AccessPolicySyncStatus.Synced
                : AccessPolicySyncStatus.Failed;
            try
            {
                await bus.RequestAsync<AccessPolicySetSyncRequestMessage, AccessPolicyOperationResponseMessage>(
                    AccessPolicySubjects.SetSync,
                    new AccessPolicySetSyncRequestMessage
                    {
                        PolicyId = policy.PolicyId,
                        Version = policy.Version,
                        Status = status,
                        Error = result.Error
                    },
                    RequestTimeout,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not persist synchronization status for policy {PolicyId}.", policy.PolicyId);
            }
        }
    }
}
