using Conduit.NATS;

namespace Scheduler.Messaging;

public sealed class NatsRequestClient(IMessageBus messageBus) : INatsRequestClient
{
    public Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => messageBus.RequestAsync<TRequest, TResponse>(subject, request, timeout, cancellationToken);
}
