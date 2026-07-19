using System.Collections.Concurrent;
using System.Threading.Channels;
using Conduit.NATS;
using Shared.Messaging;

namespace WebAPI.Features.Media;

/// <summary>
/// Singleton background service that bridges MediaProcessor's advisory rendition-progress
/// broadcast to browser SSE, mirroring <see cref="Downloads.DownloadQueueHub"/>. Everything is
/// live-only: a new subscriber receives frames from the moment it subscribes onward and
/// re-snapshots rendition status via the existing query endpoints. No re-throttling happens here —
/// MediaProcessor already gates frames at the producer.
/// </summary>
public sealed class RenditionProgressHub(IMessageBus messageBus, ILogger<RenditionProgressHub> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private ISubscription? _subscription;

    private sealed record Subscriber(Guid? MediaGuidFilter, Channel<RenditionProgress> Channel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = await messageBus.SubscribeAsync<RenditionProgress>(
            RenditionProgressSubjects.Progress,
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

    /// <summary>Subscribes to progress for every rendition, or only those of one media item.</summary>
    public (Guid Id, ChannelReader<RenditionProgress> Reader) Subscribe(Guid? mediaGuid = null)
    {
        var channel = Channel.CreateBounded<RenditionProgress>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(mediaGuid, channel);
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
            subscriber.Channel.Writer.TryComplete();
    }

    private Task HandleProgressAsync(IMessageContext<RenditionProgress> context)
    {
        var frame = context.Message;
        foreach (var (id, subscriber) in _subscribers)
        {
            if (subscriber.MediaGuidFilter is { } mediaGuid && mediaGuid != frame.MediaGuid)
                continue;

            if (!subscriber.Channel.Writer.TryWrite(frame))
                logger.LogTrace("Dropped rendition progress for {RenditionId} (subscriber {Id} channel full).", frame.RenditionId, id);
        }

        return Task.CompletedTask;
    }
}
