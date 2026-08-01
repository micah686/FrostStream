namespace Conduit.NATS;

/// <summary>
/// A single message in a JetStream batch-publish operation.
/// </summary>
/// <param name="Subject">The subject to publish to.</param>
/// <param name="Message">The message payload.</param>
/// <param name="MessageId">A stable, business-key-derived message ID for JetStream de-duplication.</param>
/// <param name="Headers">Optional headers to include with the message.</param>
public record BatchMessage<T>(
    string Subject,
    T Message,
    string MessageId,
    MessageHeaders? Headers = null);
