using System.Collections.Concurrent;
using System.Threading.Channels;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace WebAPI.Features.Downloads;

/// <summary>
/// Singleton background service that subscribes to the broadcast NATS download-progress subject
/// and fans out to per-job SSE channels registered by <see cref="Controllers.DownloadProgressController"/>.
/// </summary>
public sealed class DownloadProgressHub(IMessageBus messageBus, ILogger<DownloadProgressHub> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<DownloadProgress>> _subscribers = new();
    private ISubscription? _subscription;

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

        foreach (var (_, channel) in _subscribers)
            channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);
    }

    public ChannelReader<DownloadProgress> Subscribe(Guid jobId)
    {
        var channel = Channel.CreateBounded<DownloadProgress>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });
        if (_subscribers.TryRemove(jobId, out var old))
            old.Writer.TryComplete();
        _subscribers[jobId] = channel;
        return channel.Reader;
    }

    public void Unsubscribe(Guid jobId)
    {
        if (_subscribers.TryRemove(jobId, out var channel))
            channel.Writer.TryComplete();
    }

    private Task HandleProgressAsync(IMessageContext<DownloadProgress> context)
    {
        var progress = context.Message;
        if (_subscribers.TryGetValue(progress.JobId, out var channel))
        {
            if (!channel.Writer.TryWrite(progress))
                logger.LogTrace("Dropped progress event for job {JobId} (channel full).", progress.JobId);
        }

        return Task.CompletedTask;
    }
}
