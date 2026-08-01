namespace Conduit.NATS;

/// <summary>
/// Thin core NATS publish/subscribe/request facade over the v3 client.
/// </summary>
public interface IMessageBus
{
    /// <summary>Publishes a message to a subject.</summary>
    Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message to a subject with custom headers.</summary>
    Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to a subject and dispatches each message to <paramref name="handler"/>.
    /// Supplying a <paramref name="queueGroup"/> load-balances delivery across subscribers in that group.
    /// The underlying v3 client resubscribes automatically across reconnects.
    /// </summary>
    Task<ISubscription> SubscribeAsync<T>(string subject, Func<IMessageContext<T>, Task> handler, string? queueGroup = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and waits for a single reply, or default if none arrives within <paramref name="timeout"/>.</summary>
    Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Handle to an active core subscription.</summary>
public interface ISubscription : IAsyncDisposable
{
    Guid Id { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}
