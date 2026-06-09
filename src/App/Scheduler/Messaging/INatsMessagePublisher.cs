using FlySwattr.NATS.Abstractions;

namespace Scheduler.Messaging;

public interface INatsMessagePublisher
{
    Task PublishAsync<T>(
        string subject,
        T message,
        string? messageId,
        MessageHeaders? headers = null,
        CancellationToken cancellationToken = default);
}
