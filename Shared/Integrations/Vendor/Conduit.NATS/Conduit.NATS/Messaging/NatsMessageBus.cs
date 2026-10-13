using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace Conduit.NATS;

/// <summary>
/// Thin core NATS pub/sub/request facade over <see cref="INatsConnection"/>. The v3 client owns
/// connection lifetime, reconnect, and resubscription; this type only adapts headers, runs the
/// per-subscription dispatch loop, and tracks handles so they can be stopped on dispose.
/// </summary>
/// <remarks>
/// Core NATS is fire-and-forget: there is no ack, no broker redelivery, and no DLQ. A throwing
/// handler is logged and the message is dropped. Use JetStream consumers for at-least-once delivery.
/// </remarks>
internal sealed class NatsMessageBus : IMessageBus, IAsyncDisposable
{
    private readonly INatsConnection _connection;
    private readonly ILogger<NatsMessageBus> _logger;
    private readonly TimeSpan _defaultRequestTimeout;
    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Cts)> _subscriptions = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public NatsMessageBus(INatsConnection connection, ILogger<NatsMessageBus> logger, TimeSpan defaultRequestTimeout)
    {
        _connection = connection;
        _logger = logger;
        _defaultRequestTimeout = defaultRequestTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : defaultRequestTimeout;
    }

    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        => PublishAsync(subject, message, null, cancellationToken);

    public async Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
    {
        var natsHeaders = NatsHeaderConverter.ToNats(headers);
        await _connection.PublishAsync(subject, message, headers: natsHeaders, cancellationToken: cancellationToken);
    }

    public async Task<ISubscription> SubscribeAsync<T>(
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linkedCts.Token;

        _logger.LogInformation("Subscribing to {Subject} (queueGroup: {QueueGroup})", subject, queueGroup ?? "<none>");

        // Establish the subscription before returning so responders are registered up front. A lazy
        // SubscribeAsync(...) IAsyncEnumerable only sends the SUB once enumeration starts, which leaves
        // a startup window where request/reply callers see "no responders".
        var sub = await _connection.SubscribeCoreAsync<T>(subject, queueGroup: queueGroup, cancellationToken: token);

        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in sub.Msgs.ReadAllAsync(token))
                {
                    try
                    {
                        await handler(new CoreMessageContext<T>(msg));
                    }
                    catch (Exception ex)
                    {
                        // Core NATS has no Nack/DLQ — log and move on.
                        _logger.LogError(ex, "Handler error on subject {Subject}", msg.Subject);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Expected on stop/dispose.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscription loop for {Subject} terminated unexpectedly", subject);
            }
            finally
            {
                await sub.DisposeAsync();
            }
        }, token);

        _subscriptions[id] = (task, linkedCts);
        return new SubscriptionHandle(id, StopSubscriptionAsync);
    }

    private async Task StopSubscriptionAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_subscriptions.TryRemove(id, out var entry))
            return;

        await entry.Cts.CancelAsync();
        try
        {
            await entry.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Timed out stopping subscription {Id}", id);
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }

    public async Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout > TimeSpan.Zero ? timeout : _defaultRequestTimeout;

        // Deliberately do NOT swallow NatsNoRespondersException/timeouts into a null result: callers
        // (e.g. startup hydration) retry on exceptions to ride out a responder that isn't up yet.
        // Returning null here would turn a transient "not ready" into an immediate, unretried failure.
        var reply = await _connection.RequestAsync<TRequest, TResponse>(
            subject,
            request,
            replyOpts: new NatsSubOpts { Timeout = effectiveTimeout },
            cancellationToken: cancellationToken);
        return reply.Data;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _cts.CancelAsync();

        foreach (var id in _subscriptions.Keys)
        {
            if (_subscriptions.TryRemove(id, out var entry))
            {
                try { await entry.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                entry.Cts.Dispose();
            }
        }

        _cts.Dispose();
    }
}

internal sealed class SubscriptionHandle : ISubscription
{
    private readonly Func<Guid, CancellationToken, Task> _stop;
    private int _stopped;

    public SubscriptionHandle(Guid id, Func<Guid, CancellationToken, Task> stop)
    {
        Id = id;
        _stop = stop;
    }

    public Guid Id { get; }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => Interlocked.Exchange(ref _stopped, 1) == 0 ? _stop(Id, cancellationToken) : Task.CompletedTask;

    public ValueTask DisposeAsync() => new(StopAsync());
}

internal sealed class CoreMessageContext<T> : IMessageContext<T>
{
    private readonly NatsMsg<T> _msg;

    public CoreMessageContext(NatsMsg<T> msg) => _msg = msg;

    public T Message => _msg.Data ?? throw new InvalidOperationException("Message data is null");
    public string Subject => _msg.Subject;
    public MessageHeaders Headers => NatsHeaderConverter.FromNats(_msg.Headers);
    public string? ReplyTo => _msg.ReplyTo;

    public async Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ReplyTo))
            return;
        await _msg.ReplyAsync(response, cancellationToken: cancellationToken);
    }
}
