using Conduit.NATS;

namespace Scheduler.Messaging;

public sealed class NatsMessagePublisher(IJetStreamPublisher publisher) : INatsMessagePublisher
{
    public Task PublishAsync<T>(
        string subject,
        T message,
        string? messageId,
        MessageHeaders? headers = null,
        CancellationToken cancellationToken = default)
        => publisher.PublishAsync(subject, message, messageId, headers, cancellationToken);
}
