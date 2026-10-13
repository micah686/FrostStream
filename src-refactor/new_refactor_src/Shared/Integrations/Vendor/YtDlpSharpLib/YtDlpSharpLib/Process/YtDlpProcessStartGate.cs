using Microsoft.Extensions.Options;

namespace YtDlpSharpLib.Process;

/// <summary>
/// Serializes yt-dlp process start times without holding the gate for the process lifetime.
/// </summary>
public sealed class YtDlpProcessStartGate : IYtDlpProcessStartGate, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minimumDelay;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTimeOffset? _lastStartAt;

    public YtDlpProcessStartGate(YtDlpClientOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
        _minimumDelay = options.MinimumDelayBetweenProcessStarts is { } delay && delay > TimeSpan.Zero
            ? delay
            : TimeSpan.Zero;
    }

    public YtDlpProcessStartGate(IOptions<YtDlpClientOptions> options, TimeProvider timeProvider)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, timeProvider)
    {
    }

    public async ValueTask WaitForTurnAsync(CancellationToken ct)
    {
        if (_minimumDelay <= TimeSpan.Zero)
        {
            return;
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastStartAt is { } lastStartAt)
            {
                var now = _timeProvider.GetUtcNow();
                var nextAllowedStart = lastStartAt + _minimumDelay;
                if (nextAllowedStart > now)
                {
                    await Task.Delay(nextAllowedStart - now, _timeProvider, ct).ConfigureAwait(false);
                }
            }

            _lastStartAt = _timeProvider.GetUtcNow();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
