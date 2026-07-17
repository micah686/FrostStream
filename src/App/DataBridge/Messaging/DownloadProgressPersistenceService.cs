using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Durably persists advisory yt-dlp progress lines (<see cref="DownloadProgress.Message"/>) so the
/// Jobs page log survives a page refresh, instead of only ever reaching the browser live over SSE
/// (see <see cref="Shared.Messaging.DownloadProgress"/>'s doc comment — this subject was previously
/// unconsumed by DataBridge). Subscribes fan-out (no queue group), exactly like WebAPI's
/// DownloadQueueHub, since this is a separate, independent consumer of the same broadcast — not a
/// competing replica. Uses the same <see cref="ProgressForwardGate"/> throttle as the SSE hub so a
/// long download doesn't write hundreds of near-duplicate rows.
/// </summary>
public sealed class DownloadProgressPersistenceService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadProgressPersistenceService> logger) : SubscriptionBackgroundService
{
    private readonly ProgressForwardGate _gate = new(ProgressForwardGate.DefaultInterval);

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<DownloadProgress>(
            messageBus,
            DownloadSubjects.DownloadProgress,
            HandleProgressAsync,
            queueGroup: null,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to download progress for durable log persistence.");
    }

    private async Task HandleProgressAsync(IMessageContext<DownloadProgress> context)
    {
        var progress = context.Message;
        var message = progress.Message?.Trim();
        if (string.IsNullOrEmpty(message))
            return;
        if (!_gate.ShouldForward(progress.JobId, progress.Phase, progress.Percent))
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            await repo.AppendProgressLogAsync(progress.JobId, progress.Sequence, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed persisting progress log line for job {JobId}.", progress.JobId);
        }
    }
}
