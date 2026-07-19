using Conduit.NATS;
using Microsoft.Extensions.Logging;

namespace MediaProcessor;

/// <summary>
/// Keeps a claimed JetStream message alive during a long encode by sending in-progress acks on an
/// interval. This lets the consumers keep a short ack wait — an encoder that dies stops
/// heartbeating and the job is redelivered promptly, instead of waiting out an ack window sized
/// for the longest imaginable transcode.
/// </summary>
public sealed class JetStreamHeartbeat : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public JetStreamHeartbeat(IJsMessageContext<object> context, ILogger logger)
    {
        _loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(Interval, _cts.Token);
                    await context.InProgressAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal stop.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JetStream in-progress heartbeat stopped; the message may be redelivered while work continues.");
            }
        });
    }

    /// <summary>Stops heartbeating; call before the final ack so the two never race.</summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch
        {
            // Loop exceptions were already logged.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
