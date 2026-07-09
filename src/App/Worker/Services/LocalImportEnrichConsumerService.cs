using System.Text.Json;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Messaging;
using YtDlpSharpLib;

namespace Worker.Services;

/// <summary>
/// Optional yt-dlp metadata enrichment for import-session items that carry a source URL.
/// Runs yt-dlp with --skip-download --dump-single-json semantics (via TryGetVideoInfoAsync)
/// and publishes a compact enriched-metadata layer back to DataBridge.
/// </summary>
public sealed class LocalImportEnrichConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    ITopologyManager topologyManager,
    IYtDlpClient ytDlp,
    PotOptionsApplier potOptionsApplier,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LocalImportEnrichConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(LocalImportTopology.StreamNameValue);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        foreach (var tag in options.Tags)
        {
            await topologyManager.EnsureConsumerAsync(
                LocalImportTopology.TaggedWorkerConsumerSpec(
                    LocalImportTopology.WorkerEnrichImportSessionItemConsumer,
                    LocalImportSubjects.EnrichImportSessionItemCommand,
                    tag),
                stoppingToken);
        }

        var tasks = new List<Task>();
        if (options.AcceptsUntaggedJobs || options.Tags.Count == 0)
        {
            tasks.Add(Consume(LocalImportTopology.WorkerEnrichImportSessionItemConsumer, stoppingToken));
        }

        foreach (var tag in options.Tags)
        {
            tasks.Add(Consume($"{LocalImportTopology.WorkerEnrichImportSessionItemConsumer}-{tag}", stoppingToken));
        }

        logger.LogInformation("Subscribed to {Count} local import enrich consumer(s).", tasks.Count);
        await Task.WhenAll(tasks);
    }

    private Task Consume(string consumerName, CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<EnrichImportSessionItemCommand>(
            Stream,
            ConsumerName.From(consumerName),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<EnrichImportSessionItemCommand> context)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var heartbeatTask = JetStreamHeartbeat.RunAsync(context, HeartbeatInterval, logger, "Local import enrich", heartbeatCts.Token);
        var cmd = context.Message;
        try
        {
            await EnrichAsync(cmd);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            // Enrichment is best-effort: report the failure back instead of redelivering the
            // command, so a permanently broken URL cannot occupy the consumer for MaxDeliver rounds.
            logger.LogWarning(ex, "Enrichment failed for import session {SessionId} item {ItemId}.", cmd.SessionId, cmd.ItemId);
            await PublishFailureAsync(cmd, ErrorCode(ex), ex.Message);
            await context.AckAsync();
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { }
        }
    }

    private async Task EnrichAsync(EnrichImportSessionItemCommand cmd)
    {
        var options = YtDlpOptionsMerger.Merge(null, GetFfmpegLocation(), cookieFilePath: null, logger);
        var result = await ytDlp.TryGetVideoInfoAsync(cmd.SourceUrl, overrideOptions: potOptionsApplier.Apply(options));
        if (!result.Success || result.Data is not { } info)
        {
            await PublishFailureAsync(
                cmd,
                "enrich_fetch_failed",
                string.IsNullOrWhiteSpace(result.ErrorOutput) ? "yt-dlp metadata fetch failed." : result.ErrorOutput.Trim());
            return;
        }

        var provider = FirstNonBlank(info.Extractor, info.ExtractorKey, cmd.Provider);
        var title = FirstNonBlank(info.Title, info.FullTitle);
        var sourceMediaId = FirstNonBlank(info.Id, info.DisplayId);
        var sourceUrl = FirstNonBlank(info.WebpageUrl, cmd.SourceUrl);

        var enrichedJson = JsonSerializer.Serialize(new
        {
            title,
            provider,
            sourceMediaId,
            sourceUrl,
            description = info.Description,
            channel = FirstNonBlank(info.Channel, info.Uploader),
            channelId = FirstNonBlank(info.ChannelId, info.UploaderId),
            uploadDate = info.UploadDate,
            durationSeconds = info.Duration,
            viewCount = info.ViewCount,
            likeCount = info.LikeCount
        });

        var messageId = DeterministicGuid.Create(cmd.MessageId, "/enriched");
        await publisher.PublishAsync(
            LocalImportSubjects.ImportSessionItemEnriched,
            new ImportSessionItemEnriched
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = messageId,
                OperationKey = $"{cmd.OperationKey}/enriched",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                SessionId = cmd.SessionId,
                ItemId = cmd.ItemId,
                EnrichedMetadataJson = enrichedJson,
                Title = title,
                Provider = provider,
                SourceMediaId = sourceMediaId,
                SourceUrl = sourceUrl
            },
            messageId: messageId.ToString("N"));
    }

    private async Task PublishFailureAsync(EnrichImportSessionItemCommand cmd, string errorCode, string errorMessage)
    {
        var messageId = DeterministicGuid.Create(cmd.MessageId, "/enrich-failed");
        await publisher.PublishAsync(
            LocalImportSubjects.ImportSessionItemEnrichFailed,
            new ImportSessionItemEnrichFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = messageId,
                OperationKey = $"{cmd.OperationKey}/enrich-failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                SessionId = cmd.SessionId,
                ItemId = cmd.ItemId,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            },
            messageId: messageId.ToString("N"));
    }

    private static string? GetFfmpegLocation()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        return Directory.Exists(toolsDirectory) ? toolsDirectory : null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ErrorCode(Exception ex)
        => ex switch
        {
            TimeoutException => "enrich_timeout",
            _ => "enrich_failed"
        };
}
