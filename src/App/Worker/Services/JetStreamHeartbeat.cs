using Conduit.NATS;
using Microsoft.Extensions.Logging;

namespace Worker.Services;

internal static class JetStreamHeartbeat
{
    public static Task RunAsync<T>(
        IJsMessageContext<T> context,
        TimeSpan interval,
        ILogger logger,
        string operation,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await context.InProgressAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Operation} in-progress heartbeat failed.", operation);
            }
        }, CancellationToken.None);
    }
}
