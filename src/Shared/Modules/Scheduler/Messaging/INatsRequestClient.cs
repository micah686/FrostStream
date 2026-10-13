namespace Scheduler.Messaging;

public interface INatsRequestClient
{
    Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
