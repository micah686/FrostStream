using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Shared.Application;

namespace Shared.Messaging;

/// <summary>Full-edition acknowledgment adapter for a transport-independent durable operation.</summary>
public static class JetStreamWorkAdapter
{
    public static async Task ExecuteAsync<TRequest>(
        IJsMessageContext<TRequest> context,
        Func<TRequest, CancellationToken, Task<DurableWorkReceipt>> accept,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // AcceptAsync must return only after durable state and any applicable deduplication marker commit.
            // Keeping AckAsync here makes that ordering explicit and impossible for business code to bypass.
            await accept(context.Message, cancellationToken);
            await context.AckAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the message unacknowledged. JetStream will redeliver it after graceful shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Durable work acceptance failed for {RequestType}; requesting redelivery.",
                typeof(TRequest).Name);
            // A failure may race host shutdown. Do not let the host token suppress the
            // negative acknowledgement for a business/persistence failure.
            await context.NackAsync();
        }
    }
}
