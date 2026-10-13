namespace FrostStream.ApplicationContracts;

/// <summary>Publishes to a durable queue with caller-supplied message IDs for de-duplication.</summary>
public interface IDurableJobPublisher
{
    /// <summary>
    /// Publishes a message to a durable queue subject with a caller-supplied message ID for transport-level
    /// de-duplication across retries and restarts.
    /// </summary>
    /// <param name="messageId">
    /// <b>Required.</b> A stable, business-key-derived message ID (e.g. "Order-123-Created").
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="messageId"/> is null or whitespace.</exception>
    Task PublishAsync<T>(string subject, T message, string? messageId, MessageHeaders? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a batch of messages concurrently and awaits every ack. Failures are aggregated into an
    /// <see cref="AggregateException"/> identifying each failed message by index and message ID.
    /// </summary>
    Task PublishBatchAsync<T>(IReadOnlyList<BatchMessage<T>> messages, CancellationToken cancellationToken = default);
}

/// <summary>Consumes from a durable queue via push (subject filter) or pull (durable consumer) delivery.</summary>
public interface IDurableJobConsumer
{
    /// <summary>Consumes messages matching <paramref name="subject"/> from <paramref name="stream"/>.</summary>
    Task ConsumeAsync<T>(StreamName stream, SubjectName subject, Func<IDurableMessageContext<T>, Task> handler, DurableConsumeOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Consumes messages from a named durable <paramref name="consumer"/> for per-worker back-pressure.</summary>
    Task ConsumePullAsync<T>(StreamName stream, ConsumerName consumer, Func<IDurableMessageContext<T>, Task> handler, DurableConsumeOptions? options = null, CancellationToken cancellationToken = default);
}
