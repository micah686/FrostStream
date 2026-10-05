using System.Collections.Concurrent;

namespace Shared.Messaging;

/// <summary>
/// Per-job time-gate for advisory <see cref="DownloadProgress"/> events: passes phase changes and the
/// final (&gt;=100%) frame through immediately, otherwise throttles to at most one pass per
/// <paramref name="interval"/>. Thread-safe. Shared by every consumer of the advisory progress subject
/// (WebAPI's SSE hub, DataBridge's progress-log persistence) so they all throttle identically.
/// </summary>
public sealed class ProgressForwardGate(TimeSpan interval)
{
    /// <summary>Default spacing between forwarded/persisted progress frames for a given job.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<Guid, State> _gates = new();

    private sealed class State
    {
        public long LastForwardedTicks;
        public string? LastPhase;
    }

    public bool ShouldForward(Guid jobId, string phase, double? percent)
    {
        var gate = _gates.GetOrAdd(jobId, _ => new State { LastForwardedTicks = long.MinValue });
        var now = Environment.TickCount64;

        lock (gate)
        {
            var phaseChanged = !string.Equals(gate.LastPhase, phase, StringComparison.Ordinal);
            var isFinal = percent is >= 100;
            var due = now - gate.LastForwardedTicks >= (long)interval.TotalMilliseconds;

            if (phaseChanged || isFinal || due)
            {
                gate.LastForwardedTicks = now;
                gate.LastPhase = phase;
                return true;
            }

            return false;
        }
    }

    /// <summary>Drops throttle state for a job once it reaches a terminal/steady state.</summary>
    public void Clear(Guid jobId) => _gates.TryRemove(jobId, out _);
}
