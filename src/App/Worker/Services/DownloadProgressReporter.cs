using System.Globalization;
using Conduit.NATS;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib.Progress;

namespace Worker.Services;

internal sealed class DownloadProgressReporter(
    DownloadVideoCommand command,
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger logger) : IProgress<YtDlpProgress>
{
    private readonly object _gate = new();
    private readonly List<Task> _publishes = [];
    private int _sequence;

    public void Report(YtDlpProgress value)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var message = new DownloadProgress
        {
            JobId = command.JobId,
            CorrelationId = command.CorrelationId,
            CausationId = command.MessageId,
            MessageId = DeterministicGuid.Create(command.MessageId, $"/progress/{sequence.ToString(CultureInfo.InvariantCulture)}"),
            OperationKey = $"{command.OperationKey}/progress/{sequence.ToString(CultureInfo.InvariantCulture)}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = command.Attempt,
            Execution = command.Execution,
            Sequence = sequence,
            SourceUrl = command.SourceUrl,
            Phase = MapPhase(value.Phase),
            Percent = value.Percent,
            DownloadedBytes = value.DownloadedBytes,
            TotalBytes = value.TotalBytes,
            Speed = value.Speed,
            EtaSeconds = value.Eta?.TotalSeconds,
            Destination = value.Destination,
            Message = value.Message ?? value.AdditionalInfo
        };

        logger.LogInformation(
            "yt-dlp progress for JobId {JobId} Attempt {Attempt} Sequence {Sequence}: {Phase} {PercentText} downloaded {DownloadedText}/{TotalText} speed {Speed} ETA {EtaText}. Output: {RawLine}",
            message.JobId,
            message.Attempt,
            message.Sequence,
            message.Phase,
            FormatPercent(message.Percent),
            FormatBytes(message.DownloadedBytes),
            FormatBytes(message.TotalBytes),
            message.Speed ?? "unknown",
            FormatEta(value.Eta),
            value.RawLine ?? message.Message ?? string.Empty);

        Task publishTask;
        try
        {
            publishTask = PublishProgressAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to queue download progress publish for JobId {JobId} sequence {Sequence}",
                message.JobId,
                message.Sequence);
            return;
        }

        lock (_gate)
        {
            _publishes.Add(publishTask);
        }
    }

    /// <summary>
    /// Publishes a synthetic advisory progress frame not sourced from yt-dlp's own progress hook.
    /// Worker-driven stage details and optional-artifact warnings appear live on the Jobs page in
    /// the same way as yt-dlp's own phases.
    /// </summary>
    public Task ReportPhaseAsync(string phase, string message)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var frame = new DownloadProgress
        {
            JobId = command.JobId,
            CorrelationId = command.CorrelationId,
            CausationId = command.MessageId,
            MessageId = DeterministicGuid.Create(command.MessageId, $"/progress/{sequence.ToString(CultureInfo.InvariantCulture)}"),
            OperationKey = $"{command.OperationKey}/progress/{sequence.ToString(CultureInfo.InvariantCulture)}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = command.Attempt,
            Execution = command.Execution,
            Sequence = sequence,
            SourceUrl = command.SourceUrl,
            Phase = phase,
            Message = message
        };

        var task = PublishProgressAsync(frame);
        lock (_gate)
        {
            _publishes.Add(task);
        }
        return task;
    }

    public Task FlushAsync()
    {
        Task[] publishes;
        lock (_gate)
        {
            publishes = _publishes.ToArray();
        }

        return Task.WhenAll(publishes);
    }

    private async Task PublishProgressAsync(DownloadProgress message)
    {
        try
        {
            await publisher.PublishAsync(
                DownloadSubjects.DownloadProgress,
                message,
                messageId: message.MessageId.ToString("N"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish download progress for JobId {JobId} sequence {Sequence}",
                message.JobId,
                message.Sequence);
        }
    }

    /// <summary>
    /// yt-dlp's terminal phases only mean the *download* is done — the job still has upload and DB
    /// commit ahead of it. Renaming them keeps "Completed" reserved for the job's true end state
    /// (<see cref="DownloadJobState.Completed"/>), so the Jobs page never shows a completed-looking
    /// pill while sidecars are still uploading.
    /// </summary>
    private static string MapPhase(ProgressPhase phase) => phase switch
    {
        ProgressPhase.Finished => "DownloadFinished",
        ProgressPhase.Completed => "DownloadCompleted",
        _ => phase.ToString()
    };

    private static string FormatPercent(double? percent)
        => percent is { } value
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "unknown";

    private static string FormatEta(TimeSpan? eta)
        => eta is { } value
            ? value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
            : "unknown";

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
            return "unknown";

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
