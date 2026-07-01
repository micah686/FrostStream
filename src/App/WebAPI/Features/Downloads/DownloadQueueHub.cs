using System.Collections.Concurrent;
using System.Threading.Channels;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace WebAPI.Features.Downloads;

/// <summary>
/// Singleton background service that subscribes to the broadcast NATS download-progress subject and
/// fans out to SSE subscribers registered by the queue controller. Supports both a queue-wide stream
/// (all jobs) and per-job streams, and allows multiple concurrent subscribers for the same job.
/// Progress is live-only: a new subscriber receives events from the moment it subscribes onward.
/// </summary>
public sealed class DownloadQueueHub(IMessageBus messageBus, ILogger<DownloadQueueHub> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private ISubscription? _subscription;

    private sealed record Subscriber(Guid? JobFilter, Channel<DownloadProgress> Channel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<DownloadProgress>(
            DownloadSubjects.DownloadProgress,
            HandleProgressAsync,
            queueGroup: null,
            cancellationToken: stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.StopAsync(cancellationToken);
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        foreach (var (_, subscriber) in _subscribers)
            subscriber.Channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);
    }

    /// <summary>Subscribes to progress for a single job. Multiple subscribers per job are allowed.</summary>
    public (Guid Id, ChannelReader<DownloadProgress> Reader) SubscribeToJob(Guid jobId) => Register(jobId);

    /// <summary>Subscribes to progress for every job (queue-wide stream).</summary>
    public (Guid Id, ChannelReader<DownloadProgress> Reader) SubscribeToQueue() => Register(null);

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
            subscriber.Channel.Writer.TryComplete();
    }

    private (Guid Id, ChannelReader<DownloadProgress> Reader) Register(Guid? jobFilter)
    {
        var channel = Channel.CreateBounded<DownloadProgress>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(jobFilter, channel);
        return (id, channel.Reader);
    }

    private Task HandleProgressAsync(IMessageContext<DownloadProgress> context)
    {
        var progress = context.Message;
        foreach (var (id, subscriber) in _subscribers)
        {
            if (subscriber.JobFilter is { } jobId && jobId != progress.JobId)
                continue;

            if (!subscriber.Channel.Writer.TryWrite(progress))
                logger.LogTrace("Dropped progress event for job {JobId} (subscriber {Id} channel full).", progress.JobId, id);
        }

        return Task.CompletedTask;
    }
}
