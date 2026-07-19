using System.Globalization;
using Conduit.NATS;
using MediaProcessor.Ffmpeg;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace MediaProcessor;

/// <summary>
/// Publishes advisory <see cref="RenditionProgress"/> frames for one claimed rendition, throttled
/// at the producer with the same <see cref="ProgressForwardGate"/> the download pipeline uses
/// (phase changes and the final frame always pass; percent updates at most every 500 ms).
/// Publish failures are swallowed — progress must never fail an encode. Also logs encode progress
/// on an interval so a plain service log tail shows liveness without any SSE subscriber attached.
/// </summary>
internal sealed class RenditionProgressReporter(
    IMessageBus messageBus,
    IClock clock,
    ILogger logger,
    Guid renditionId,
    RenditionKind kind,
    Guid mediaGuid)
{
    private static readonly TimeSpan EncodeLogInterval = TimeSpan.FromSeconds(30);

    private readonly ProgressForwardGate _gate = new(ProgressForwardGate.DefaultInterval);
    private int _sequence;
    private long _lastEncodeLogTicks = long.MinValue;

    public Task PhaseAsync(string phase, double? percent = null, string? message = null)
        => PublishAsync(phase, percent, speedX: null, etaSeconds: null, message);

    /// <summary>
    /// Callback for ffmpeg <c>-progress</c> frames. Fire-and-forget: it runs on the process stdout
    /// pump, which must never block on NATS.
    /// </summary>
    public void ReportFfmpeg(string phase, FfmpegProgress progress, int? sourceDurationSeconds)
    {
        double? percent = null;
        double? etaSeconds = null;
        if (sourceDurationSeconds is > 0)
        {
            percent = Math.Clamp(progress.OutTimeSeconds / sourceDurationSeconds.Value * 100d, 0d, 100d);
            if (progress.SpeedX is > 0)
                etaSeconds = Math.Max(0d, sourceDurationSeconds.Value - progress.OutTimeSeconds) / progress.SpeedX.Value;
        }

        var now = Environment.TickCount64;
        if (now - _lastEncodeLogTicks >= (long)EncodeLogInterval.TotalMilliseconds)
        {
            _lastEncodeLogTicks = now;
            logger.LogInformation(
                "{Kind} rendition {RenditionId} {Phase}: {PercentText} at {SpeedText}, ETA {EtaText}.",
                kind,
                renditionId,
                phase,
                percent is { } p ? p.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "unknown %",
                progress.SpeedX is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) + "x" : "unknown speed",
                etaSeconds is { } e ? TimeSpan.FromSeconds(e).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : "unknown");
        }

        _ = PublishAsync(phase, percent, progress.SpeedX, etaSeconds, message: null);
    }

    private async Task PublishAsync(string phase, double? percent, double? speedX, double? etaSeconds, string? message)
    {
        if (!_gate.ShouldForward(renditionId, phase, percent))
            return;

        var frame = new RenditionProgress
        {
            RenditionId = renditionId,
            Kind = kind,
            MediaGuid = mediaGuid,
            Sequence = Interlocked.Increment(ref _sequence),
            OccurredAt = clock.GetCurrentInstant(),
            Phase = phase,
            Percent = percent,
            SpeedX = speedX,
            EtaSeconds = etaSeconds,
            Message = message
        };

        try
        {
            await messageBus.PublishAsync(RenditionProgressSubjects.Progress, frame);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed publishing rendition progress for {RenditionId} (advisory only).", renditionId);
        }
    }
}
