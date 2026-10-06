using System.Threading.Channels;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging;

namespace Shared.Messaging.Adapters;

/// <summary>
/// In-process requests and transient events. Each subscription has a bounded, ordered queue.
/// Publishers wait for space; cancellation or stopping a subscription releases blocked writers.
/// Events are not persisted. Durable work belongs to IDurableJobPublisher.
/// </summary>
public sealed class LocalApplicationTransport(ILogger<LocalApplicationTransport> logger, int capacity = 256) : IMessageBus, IAsyncDisposable
{
    private readonly ILogger<LocalApplicationTransport> _logger = logger;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Dictionary<string, long> _roundRobin = [];
    private bool _disposed;

    public Task<ISubscription> SubscribeAsync<T>(string subject, Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, subject, typeof(T), queueGroup,
                envelope => handler(new Context<T>(envelope)), capacity, cancellationToken);
            _subscriptions.Add(subscription);
            return Task.FromResult<ISubscription>(subscription);
        }
    }

    private Subscription[] Select(string subject, Type type)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var matches = _subscriptions.Where(s => !s.Stopped && s.Type == type && Matches(s.Subject, subject)).ToArray();
            var selected = matches.Where(s => s.Group is null).ToList();
            foreach (var group in matches.Where(s => s.Group is not null).GroupBy(s => s.Group!))
            {
                var key = group.Key;
                var index = _roundRobin.GetValueOrDefault(key);
                _roundRobin[key] = index == long.MaxValue ? 0 : index + 1;
                selected.Add(group.ElementAt((int)(index % group.Count())));
            }
            return selected.ToArray();
        }
    }

    private static bool Matches(string pattern, string subject)
    {
        var p = pattern.Split('.');
        var s = subject.Split('.');
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == ">") return i == p.Length - 1 && i < s.Length;
            if (i >= s.Length || (p[i] != "*" && p[i] != s[i])) return false;
        }
        return p.Length == s.Length;
    }

    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        => PublishAsync(subject, message, null, cancellationToken);

    public async Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        cancellationToken.ThrowIfCancellationRequested();
        var subscribers = Select(subject, typeof(T));
        await Task.WhenAll(subscribers.Select(s => s.EnqueueAsync(
            new Envelope(subject, message, new MessageHeaders(new(headers?.Headers ?? [])), null), cancellationToken)));
    }

    public async Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var reply = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = Select(subject, typeof(TRequest)).FirstOrDefault();
        if (subscriber is not null)
        {
            // Invoke the existing handler directly; nested requests never wait on the event queue.
            _ = InvokeAsync();
        }
        try
        {
            var result = await reply.Task.WaitAsync(deadline.Token);
            return result is null ? default : (TResponse)result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }

        async Task InvokeAsync()
        {
            try { await subscriber!.Handler(new Envelope(subject, request, MessageHeaders.Empty, reply)); }
            catch (Exception exception) { reply.TrySetException(exception); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions.ToArray();
        }
        foreach (var subscription in subscriptions) await subscription.DisposeAsync();
    }

    private sealed record Envelope(string Subject, object? Message, MessageHeaders Headers, TaskCompletionSource<object?>? Reply);

    private sealed class Context<T>(Envelope envelope) : IMessageContext<T>
    {
        public T Message => (T)envelope.Message!;
        public string Subject => envelope.Subject;
        public MessageHeaders Headers => envelope.Headers;
        public string? ReplyTo => envelope.Reply is null ? null : "local.reply";
        public Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            envelope.Reply?.TrySetResult(response);
            return Task.CompletedTask;
        }
    }

    private sealed class Subscription : ISubscription
    {
        private readonly LocalApplicationTransport _owner;
        private readonly Channel<Envelope> _channel;
        private readonly CancellationTokenSource _stop;
        private readonly Task _pump;
        public Guid Id { get; } = Guid.NewGuid();
        public string Subject { get; }
        public Type Type { get; }
        public string? Group { get; }
        public Func<Envelope, Task> Handler { get; }
        public bool Stopped => _stop.IsCancellationRequested;

        public Subscription(LocalApplicationTransport owner, string subject, Type type, string? group,
            Func<Envelope, Task> handler, int capacity, CancellationToken cancellationToken)
        {
            _owner = owner;
            Subject = subject;
            Type = type;
            Group = group;
            Handler = handler;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false
            });
            _pump = PumpAsync();
        }

        public async Task EnqueueAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            try { await _channel.Writer.WriteAsync(envelope, linked.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Stopped) { }
            catch (ChannelClosedException) when (Stopped) { }
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var envelope in _channel.Reader.ReadAllAsync(_stop.Token))
                {
                    if (Stopped) break;
                    try { await Handler(envelope); }
                    catch (Exception exception) { _owner._logger.LogError(exception, "Local notification handler failed for {Subject}", envelope.Subject); }
                }
            }
            catch (OperationCanceledException) when (Stopped) { }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stop.Cancel();
            _channel.Writer.TryComplete();
            lock (_owner._gate) _owner._subscriptions.Remove(this);
            await _pump.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
