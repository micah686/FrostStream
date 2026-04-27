using System.Text.Json;
using DataBridge.Data;
using DataBridge.Flows;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Consumes Worker-emitted result events for the download flow.
/// For each event:
///   1. Dedupe via <c>processed_messages</c>.
///   2. Persist the business-visible state change (<c>download_jobs</c>) and append
///      to <c>download_job_history</c>.
///   3. Forward the event to the Cleipnir flow keyed by <see cref="IFlowMessage.JobId"/>.
///   4. Mark the message as processed.
///
/// All work runs under a single fresh DI scope per message so the scoped
/// <see cref="DataBridgeDbContext"/> isn't shared across messages.
/// </summary>
public sealed class DownloadEventsConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    DownloadArchiveFlows flows,
    ILogger<DownloadEventsConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "databridge-downloads";

    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Subscribe<MetadataFetched>(DownloadSubjects.MetadataFetched, ApplyMetadataFetchedAsync, stoppingToken);
        await Subscribe<MetadataFetchFailed>(DownloadSubjects.MetadataFetchFailed, NoStateChange, stoppingToken);
        await Subscribe<DownloadCompleted>(DownloadSubjects.DownloadCompleted, ApplyDownloadCompletedAsync, stoppingToken);
        await Subscribe<DownloadFailed>(DownloadSubjects.DownloadFailed, NoStateChange, stoppingToken);
        await Subscribe<UploadCompleted>(DownloadSubjects.UploadCompleted, ApplyUploadCompletedAsync, stoppingToken);
        await Subscribe<UploadFailed>(DownloadSubjects.UploadFailed, NoStateChange, stoppingToken);
        await Subscribe<TempFileDeleted>(DownloadSubjects.TempFileDeleted, NoStateChange, stoppingToken);
        await Subscribe<TempFileDeleteFailed>(DownloadSubjects.TempFileDeleteFailed, NoStateChange, stoppingToken);
        await Subscribe<UploadedObjectDeleted>(DownloadSubjects.UploadedObjectDeleted, NoStateChange, stoppingToken);
        await Subscribe<UploadedObjectDeleteFailed>(DownloadSubjects.UploadedObjectDeleteFailed, NoStateChange, stoppingToken);

        logger.LogInformation("Subscribed to {Count} download event subjects.", _subscriptions.Count);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task Subscribe<TEvent>(
        string subject,
        Func<IDownloadJobsRepository, TEvent, CancellationToken, Task> persist,
        CancellationToken stoppingToken)
        where TEvent : class, IFlowMessage
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<TEvent>(
            subject,
            ctx => HandleEventAsync(ctx, persist),
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));
    }

    private async Task HandleEventAsync<TEvent>(
        IMessageContext<TEvent> context,
        Func<IDownloadJobsRepository, TEvent, CancellationToken, Task> persist)
        where TEvent : class, IFlowMessage
    {
        var evt = context.Message;
        var eventName = typeof(TEvent).Name;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();

            if (await jobs.IsMessageProcessedAsync(evt.MessageId))
            {
                return;
            }

            await persist(jobs, evt, CancellationToken.None);

            await jobs.RecordHistoryAsync(
                evt.JobId,
                evt.MessageId,
                evt.OperationKey,
                eventName,
                JsonSerializer.Serialize<TEvent>(evt));

            await flows.SendMessage(evt.JobId.ToString("N"), evt);

            await jobs.MarkMessageProcessedAsync(evt.MessageId, evt.OperationKey, evt.JobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling {EventName} for JobId {JobId}", eventName, evt.JobId);
            throw;
        }
    }

    private static Task ApplyMetadataFetchedAsync(IDownloadJobsRepository jobs, MetadataFetched evt, CancellationToken ct)
        => jobs.ApplyMetadataAsync(evt.JobId, evt, ct);

    private static Task ApplyDownloadCompletedAsync(IDownloadJobsRepository jobs, DownloadCompleted evt, CancellationToken ct)
        => jobs.ApplyDownloadCompletedAsync(evt.JobId, evt, ct);

    private static Task ApplyUploadCompletedAsync(IDownloadJobsRepository jobs, UploadCompleted evt, CancellationToken ct)
        => jobs.CommitUploadAsync(evt.JobId, evt, ct);

    private static Task NoStateChange<TEvent>(IDownloadJobsRepository jobs, TEvent evt, CancellationToken ct)
        where TEvent : IFlowMessage
        => Task.CompletedTask;
}
