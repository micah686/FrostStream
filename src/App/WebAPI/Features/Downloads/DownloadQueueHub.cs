using System.Collections.Concurrent;
using System.Threading.Channels;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace WebAPI.Features.Downloads;

/// <summary>One item on a queue SSE stream: either a live progress frame or a state transition.</summary>
public abstract record QueueStreamEvent
{
    public abstract Guid JobId { get; }

    public sealed record Progress(DownloadProgress Value) : QueueStreamEvent
    {
        public override Guid JobId => Value.JobId;
    }

    public sealed record State(DownloadQueueStateChanged Value) : QueueStreamEvent
    {
        public override Guid JobId => Value.JobId;
    }
}

/// <summary>
/// Singleton background service that bridges the internal NATS download signals to browser SSE.
/// Subscribes to the broadcast progress subject and the (non-persistent) state-changed subject and
/// fans both to SSE subscribers — a queue-wide stream (all jobs) and per-job streams, with multiple
/// concurrent subscribers allowed. Everything is live-only: a new subscriber receives events from the
/// moment it subscribes onward (the client re-snapshots via <c>GET /queue</c> on connect/reconnect).
/// Progress is coalesced to at most one frame per job per <see cref="ProgressInterval"/>; state and
/// terminal/phase-change progress frames are never dropped.
/// </summary>
public sealed class DownloadQueueHub(IMessageBus messageBus, ILogger<DownloadQueueHub> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ProgressForwardGate _progressGate = new(ProgressForwardGate.DefaultInterval);
    private ISubscription? _progressSubscription;
    private ISubscription? _stateSubscription;

    private sealed record Subscriber(Guid? JobFilter, Channel<QueueStreamEvent> Channel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _progressSubscription = await messageBus.SubscribeAsync<DownloadProgress>(
            DownloadSubjects.DownloadProgress,
            HandleProgressAsync,
            queueGroup: null,
            cancellationToken: stoppingToken);

        _stateSubscription = await messageBus.SubscribeAsync<DownloadQueueStateChanged>(
            DownloadQueueSubjects.StateChanged,
            HandleStateAsync,
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
        foreach (var subscription in new[] { _progressSubscription, _stateSubscription })
        {
            if (subscription is not null)
            {
                await subscription.StopAsync(cancellationToken);
                await subscription.DisposeAsync();
            }
        }
        _progressSubscription = null;
        _stateSubscription = null;

        foreach (var (_, subscriber) in _subscribers)
            subscriber.Channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);
    }

    /// <summary>Subscribes to events for a single job. Multiple subscribers per job are allowed.</summary>
    public (Guid Id, ChannelReader<QueueStreamEvent> Reader) SubscribeToJob(Guid jobId) => Register(jobId);

    /// <summary>Subscribes to events for every job (queue-wide stream).</summary>
    public (Guid Id, ChannelReader<QueueStreamEvent> Reader) SubscribeToQueue() => Register(null);

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var subscriber))
            subscriber.Channel.Writer.TryComplete();
    }

    private (Guid Id, ChannelReader<QueueStreamEvent> Reader) Register(Guid? jobFilter)
    {
        var channel = Channel.CreateBounded<QueueStreamEvent>(new BoundedChannelOptions(256)
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
        if (_progressGate.ShouldForward(progress.JobId, progress.Phase, progress.Percent))
            Fan(new QueueStreamEvent.Progress(progress));
        return Task.CompletedTask;
    }

    private Task HandleStateAsync(IMessageContext<DownloadQueueStateChanged> context)
    {
        // A job that reached a terminal/steady state won't emit more progress; drop its throttle state.
        _progressGate.Clear(context.Message.JobId);
        Fan(new QueueStreamEvent.State(context.Message));
        return Task.CompletedTask;
    }

    private void Fan(QueueStreamEvent evt)
    {
        foreach (var (id, subscriber) in _subscribers)
        {
            if (subscriber.JobFilter is { } jobId && jobId != evt.JobId)
                continue;

            if (!subscriber.Channel.Writer.TryWrite(evt))
                logger.LogTrace("Dropped queue event for job {JobId} (subscriber {Id} channel full).", evt.JobId, id);
        }
    }
}
