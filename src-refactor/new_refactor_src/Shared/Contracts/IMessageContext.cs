namespace FrostStream.ApplicationContracts;

/// <summary>Context handed to a core subscription handler for a single received message.</summary>
public interface IMessageContext<out T>
{
    T Message { get; }
    string Subject { get; }
    MessageHeaders Headers { get; }
    string? ReplyTo { get; }

    Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default);
}

/// <summary>Context handed to a durable queue consumer handler, adding ack/redelivery control and metadata.</summary>
public interface IDurableMessageContext<out T> : IMessageContext<T>
{
    Task AckAsync(CancellationToken cancellationToken = default);
    Task NackAsync(TimeSpan? delay = null, CancellationToken cancellationToken = default);
    Task TermAsync(CancellationToken cancellationToken = default);
    Task InProgressAsync(CancellationToken cancellationToken = default);

    ulong Sequence { get; }
    DateTimeOffset Timestamp { get; }
    bool Redelivered { get; }
    uint NumDelivered { get; }
}
