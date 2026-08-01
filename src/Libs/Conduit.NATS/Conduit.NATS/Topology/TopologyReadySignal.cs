using Microsoft.Extensions.Logging;

namespace Conduit.NATS;

/// <summary><see cref="TaskCompletionSource"/>-backed <see cref="ITopologyReadySignal"/> for startup sequencing.</summary>
internal sealed class TopologyReadySignal : ITopologyReadySignal
{
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<TopologyReadySignal> _logger;
    private int _signaled;

    public TopologyReadySignal(ILogger<TopologyReadySignal> logger) => _logger = logger;

    public bool IsSignaled => _signaled != 0;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (_tcs.Task.IsCompleted)
        {
            await _tcs.Task;
            return;
        }

        using var registration = cancellationToken.Register(
            () => _tcs.TrySetCanceled(cancellationToken),
            useSynchronizationContext: false);
        await _tcs.Task;
    }

    public void SignalReady()
    {
        if (Interlocked.CompareExchange(ref _signaled, 1, 0) == 0)
        {
            _logger.LogInformation("Topology ready signal dispatched.");
            _tcs.TrySetResult(true);
        }
    }

    public void SignalFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.CompareExchange(ref _signaled, 1, 0) == 0)
        {
            _logger.LogError(exception, "Topology provisioning failed.");
            _tcs.TrySetException(exception);
        }
    }
}
