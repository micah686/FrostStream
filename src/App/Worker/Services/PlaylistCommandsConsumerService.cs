using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Models;

namespace Worker.Services;

/// <summary>
/// Worker-side JetStream consumer for the playlist pipeline. Currently handles only the
/// flat-metadata fetch — per-entry downloads are dispatched as ordinary <c>DownloadRequested</c>
/// messages by DataBridge, and the per-job command consumers in
/// <see cref="DownloadCommandsConsumerService"/> handle them just like any other download.
/// </summary>
public sealed class PlaylistCommandsConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    IYtDlpClient ytDlp,
    IClock clock,
    ILogger<PlaylistCommandsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(PlaylistTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = consumer.ConsumePullAsync<FetchPlaylistMetadataCommand>(
            stream: Stream,
            consumer: ConsumerName.From(PlaylistTopology.WorkerFetchPlaylistMetadataConsumer),
            handler: HandleFetchPlaylistMetadataAsync,
            options: null,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to playlist command consumers on stream {Stream}.", Stream.Value);
        return task;
    }

    private async Task HandleFetchPlaylistMetadataAsync(IJsMessageContext<FetchPlaylistMetadataCommand> context)
    {
        var cmd = context.Message;

        try
        {
            var entries = new List<PlaylistEntry>();
            string? title = null;
            string? providerPlaylistId = null;

            // First, get the top-level playlist metadata (with flat entries embedded).
            var topResult = await ytDlp.TryGetVideoInfoAsync(cmd.SourceUrl, flat: true);
            if (topResult.Success && topResult.Data is { } container)
            {
                title = container.PlaylistTitle ?? container.Title;
                providerPlaylistId = container.PlaylistId ?? container.Id;

                if (container.Entries is { Count: > 0 } childEntries)
                {
                    var index = 1;
                    foreach (var entry in childEntries)
                    {
                        var url = entry.WebpageUrl ?? entry.Url;
                        if (string.IsNullOrWhiteSpace(url))
                            continue;
                        var resolvedIndex = entry.PlaylistIndex ?? index;
                        entries.Add(new PlaylistEntry
                        {
                            PlaylistIndex = resolvedIndex,
                            EntryUrl = url!,
                            EntryTitle = entry.Title ?? entry.FullTitle
                        });
                        index++;
                    }
                }
                else
                {
                    // Fall back to the streaming flat-playlist endpoint when the top-level
                    // dump did not embed the entries (some extractors return them piecemeal).
                    await foreach (var entry in ytDlp.GetPlaylistInfoAsync(cmd.SourceUrl))
                    {
                        var url = entry.WebpageUrl ?? entry.Url;
                        if (string.IsNullOrWhiteSpace(url))
                            continue;
                        var resolvedIndex = entry.PlaylistIndex ?? entries.Count + 1;
                        entries.Add(new PlaylistEntry
                        {
                            PlaylistIndex = resolvedIndex,
                            EntryUrl = url!,
                            EntryTitle = entry.Title ?? entry.FullTitle
                        });
                    }
                }
            }
            else
            {
                throw new YtDlpProcessException(
                    $"yt-dlp playlist metadata fetch failed for {cmd.SourceUrl}",
                    command: null,
                    exitCode: null,
                    lastStderrLines: topResult.ErrorOutput);
            }

            if (entries.Count == 0)
            {
                throw new YtDlpProcessException(
                    $"yt-dlp returned no entries for playlist URL {cmd.SourceUrl}",
                    command: null,
                    exitCode: null,
                    lastStderrLines: string.Empty);
            }

            await Publish(PlaylistSubjects.PlaylistMetadataFetched, new PlaylistMetadataFetched
            {
                PlaylistId = cmd.PlaylistId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                ProviderPlaylistId = providerPlaylistId,
                Title = title,
                TotalItems = entries.Count,
                Entries = entries
            });
            await context.AckAsync();
        }
        catch (YtDlpUnavailableException ex)
        {
            logger.LogWarning(ex,
                "FetchPlaylistMetadata: source unavailable for PlaylistId {PlaylistId} URL {SourceUrl}",
                cmd.PlaylistId, cmd.SourceUrl);
            await PublishFailedAsync(cmd, ex, YtDlpFailureDetails.ClassifyYtDlpFailure(ex));
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "FetchPlaylistMetadata failed for PlaylistId {PlaylistId} URL {SourceUrl}",
                cmd.PlaylistId, cmd.SourceUrl);
            await PublishFailedAsync(cmd, ex, YtDlpFailureDetails.ClassifyFailure(ex));
            await context.AckAsync();
        }
    }

    private Task PublishFailedAsync(FetchPlaylistMetadataCommand cmd, Exception ex, FailureKind failureKind)
        => Publish(PlaylistSubjects.PlaylistMetadataFetchFailed, new PlaylistMetadataFetchFailed
        {
            PlaylistId = cmd.PlaylistId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = failureKind,
            ErrorCode = YtDlpFailureDetails.ErrorCode(ex),
            ErrorMessage = YtDlpFailureDetails.DescribeException(ex)
        });

    private Task Publish<T>(string subject, T message) where T : IPlaylistFlowMessage
        => publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));
}
