namespace Conduit.NATS;

/// <summary>
/// Configuration for consuming messages from JetStream.
/// </summary>
public record JetStreamConsumeOptions
{
    /// <summary>
    /// The durable JetStream consumer name to bind to. If null, an ephemeral consumer is created.
    /// </summary>
    public ConsumerName? DurableName { get; init; }

    /// <summary>
    /// Optional queue delivery group, controlling load-balanced delivery semantics
    /// distinct from the durable consumer identity.
    /// </summary>
    public QueueGroup? DeliverGroup { get; init; }

    /// <summary>Maximum number of messages to process concurrently within this consumer.</summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>Batch size (max in-flight pull) for the consume loop. Default: 10.</summary>
    public int BatchSize { get; init; } = 10;

    /// <summary>Optional server-side cap on unacknowledged messages for the consumer.</summary>
    public int? MaxAckPending { get; init; }

    public static JetStreamConsumeOptions Default => new();
}
