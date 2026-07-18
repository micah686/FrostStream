using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;

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
    PotOptionsApplier potOptionsApplier,
    IClock clock,
    ILogger<PlaylistCommandsConsumerService> logger) : BackgroundService
{
    internal const int MaxPlaylistEntriesPerRequest = 5_000;

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
            var pageStartIndex = PageStartIndex(cmd);
            var pageSize = PageSize(cmd);

            var playlistOptions = BuildPlaylistOptions(pageStartIndex, pageSize);

            // First, get the top-level playlist metadata (with flat entries embedded).
            var topResult = await ytDlp.TryGetVideoInfoAsync(cmd.SourceUrl, flat: true, overrideOptions: potOptionsApplier.Apply(playlistOptions));
            if (topResult.Success && topResult.Data is { } container)
            {
                title = container.PlaylistTitle ?? container.Title;
                providerPlaylistId = container.PlaylistId ?? container.Id;

                if (container.Entries is { Count: > 0 } childEntries)
                {
                    var fallbackIndex = pageStartIndex;
                    foreach (var entry in childEntries.Take(pageSize))
                    {
                        var url = ResolveEntryUrl(cmd.SourceUrl, entry);
                        if (string.IsNullOrWhiteSpace(url))
                            continue;
                        var resolvedIndex = entry.PlaylistIndex ?? fallbackIndex;
                        entries.Add(new PlaylistEntry
                        {
                            PlaylistIndex = resolvedIndex,
                            EntryUrl = url!,
                            EntryTitle = entry.Title ?? entry.FullTitle
                        });
                        fallbackIndex++;
                    }
                }
                else
                {
                    // Fall back to the streaming flat-playlist endpoint when the top-level
                    // dump did not embed the entries (some extractors return them piecemeal).
                    var streamedIndex = 0;
                    await foreach (var entry in ytDlp.GetPlaylistInfoAsync(cmd.SourceUrl, overrideOptions: potOptionsApplier.Apply(null)))
                    {
                        streamedIndex++;
                        var resolvedIndex = entry.PlaylistIndex ?? streamedIndex;
                        if (resolvedIndex < pageStartIndex)
                            continue;

                        if (entries.Count >= pageSize)
                            break;

                        var url = ResolveEntryUrl(cmd.SourceUrl, entry);
                        if (string.IsNullOrWhiteSpace(url))
                            continue;

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

            if (entries.Count == 0 && pageStartIndex == FetchPlaylistMetadataCommandDefaults.PageStartIndex)
            {
                throw new YtDlpProcessException(
                    $"yt-dlp returned no entries for playlist URL {cmd.SourceUrl}",
                    command: null,
                    exitCode: null,
                    lastStderrLines: string.Empty);
            }

            var totalItems = topResult.Data?.PlaylistCount ?? 0;
            var nextPageStartIndex = pageStartIndex + pageSize;
            var pageEndIndex = pageStartIndex + entries.Count - 1;
            var isComplete = entries.Count < pageSize;

            if (!isComplete)
            {
                logger.LogInformation(
                    "Playlist {PlaylistId} URL {SourceUrl} staged page {PageStartIndex}:{PageEndIndex}; requesting next page from {NextPageStartIndex}.",
                    cmd.PlaylistId,
                    cmd.SourceUrl,
                    pageStartIndex,
                    pageEndIndex,
                    nextPageStartIndex);
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
                TotalItems = totalItems > 0 ? totalItems : Math.Max(0, pageEndIndex),
                PageStartIndex = pageStartIndex,
                PageSize = pageSize,
                IsComplete = isComplete,
                NextPageStartIndex = isComplete ? null : nextPageStartIndex,
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

    internal static YtDlpOptions BuildPlaylistOptions(
        int pageStartIndex = FetchPlaylistMetadataCommandDefaults.PageStartIndex,
        int pageSize = FetchPlaylistMetadataCommandDefaults.PageSize)
    {
        pageStartIndex = Math.Max(FetchPlaylistMetadataCommandDefaults.PageStartIndex, pageStartIndex);
        pageSize = Math.Clamp(pageSize, 1, MaxPlaylistEntriesPerRequest);
        var pageEndIndex = pageStartIndex + pageSize - 1;

        return new YtDlpOptions
        {
            VideoSelection = new YtDlpVideoSelectionOptions
            {
                PlaylistItems = $"{pageStartIndex}:{pageEndIndex}"
            }
        };
    }

    private static int PageStartIndex(FetchPlaylistMetadataCommand cmd)
        => Math.Max(FetchPlaylistMetadataCommandDefaults.PageStartIndex, cmd.PageStartIndex);

    private static int PageSize(FetchPlaylistMetadataCommand cmd)
        => Math.Clamp(cmd.PageSize, 1, MaxPlaylistEntriesPerRequest);

    internal static string? ResolveEntryUrl(string collectionUrl, VideoInfo entry)
    {
        if (IsYouTubeUrl(collectionUrl) && !string.IsNullOrWhiteSpace(entry.Id))
            return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(entry.Id)}";

        return FirstIndividualAbsoluteUrl(collectionUrl, entry.WebpageUrl, entry.Url);
    }

    private static bool IsYouTubeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));

    private static string? FirstIndividualAbsoluteUrl(string collectionUrl, params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out _)
            && !string.Equals(
                value.Trim().TrimEnd('/'),
                collectionUrl.Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase));
}
