using System.Globalization;
using FlySwattr.NATS.Abstractions;
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
            Sequence = sequence,
            SourceUrl = command.SourceUrl,
            Phase = value.Phase.ToString(),
            Percent = value.Percent,
            DownloadedBytes = value.DownloadedBytes,
            TotalBytes = value.TotalBytes,
            Speed = value.Speed,
            EtaSeconds = value.Eta?.TotalSeconds,
            Destination = value.Destination,
            Message = value.Message ?? value.AdditionalInfo
        };

        logger.LogInformation(
            "Download progress for JobId {JobId}: phase {Phase}, percent {Percent}, downloaded {DownloadedBytes}/{TotalBytes}, speed {Speed}, ETA {EtaSeconds}s",
            message.JobId,
            message.Phase,
            message.Percent,
            message.DownloadedBytes,
            message.TotalBytes,
            message.Speed,
            message.EtaSeconds);

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
                messageId: message.MessageId.ToString("N")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish download progress for JobId {JobId} sequence {Sequence}",
                message.JobId,
                message.Sequence);
        }
    }
}
