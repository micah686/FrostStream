using System.Text.Json;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Imports;
using Shared.Messaging;
using YtDlpSharpLib;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Options;

namespace Worker.Services;

/// <summary>
/// Optional yt-dlp metadata enrichment for import-session items that carry a source URL.
/// Runs yt-dlp with --write-info-json --skip-download, writes the sidecar beside the source
/// media, and publishes the complete metadata layer back to DataBridge.
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
        var sourcePath = ResolveIncomingPath(cmd.RelativePath);
        var outputDirectory = Path.GetDirectoryName(sourcePath)
                              ?? throw new InvalidOperationException("The source media folder could not be resolved.");
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var infoJsonPath = Path.Combine(outputDirectory, $"{stem}.info.json");
        var userOptions = BuildOptions(cmd.Options);
        var options = potOptionsApplier.Apply(YtDlpOptionsMerger.Merge(userOptions, GetFfmpegLocation(), cookieFilePath: null, logger))
                      ?? userOptions;

        await ytDlp.DownloadMetadataAsync(
            cmd.SourceUrl,
            outputDirectory,
            new MetadataDownloadOptions
            {
                YtDlp = options,
                OutputTemplate = $"{stem.Replace("%", "%%", StringComparison.Ordinal)}.%(ext)s",
                OverwriteFiles = true,
                IgnoreDownloadErrors = false
            });

        if (!File.Exists(infoJsonPath))
        {
            infoJsonPath = Directory.EnumerateFiles(outputDirectory, $"{stem}*.info.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new FileNotFoundException("yt-dlp completed without writing the expected info.json sidecar.");
        }

        var enrichedJson = await File.ReadAllTextAsync(infoJsonPath);
        using var document = JsonDocument.Parse(enrichedJson);
        var root = document.RootElement;
        var provider = FirstNonBlank(ReadString(root, "extractor"), ReadString(root, "extractor_key"), cmd.Provider);
        var title = FirstNonBlank(ReadString(root, "title"), ReadString(root, "fulltitle"));
        var sourceMediaId = FirstNonBlank(ReadString(root, "id"), ReadString(root, "display_id"));
        var sourceUrl = FirstNonBlank(ReadString(root, "webpage_url"), ReadString(root, "original_url"), cmd.SourceUrl);
        var relativeInfoPath = Path.GetRelativePath(workerOptions.Value.IncomingRoot, infoJsonPath)
            .Replace(Path.DirectorySeparatorChar, '/');

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
                SourceUrl = sourceUrl,
                InfoJsonRelativePath = relativeInfoPath
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

    private string ResolveIncomingPath(string relativePath)
    {
        var incomingRoot = workerOptions.Value.IncomingRoot;
        if (!LocalImportPathRules.TryResolveUnderAllowedRoots(
                incomingRoot,
                relativePath,
                [incomingRoot],
                out var fullPath,
                out _,
                out var error))
        {
            throw new ArgumentException(error, nameof(relativePath));
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Local import file was not found.", fullPath);
        return fullPath;
    }

    private static YtDlpOptions BuildOptions(ImportSessionYtDlpOptions options)
        => new()
        {
            Network = new YtDlpNetworkOptions { Proxy = Normalize(options.ProxyUrl) },
            Authentication = new YtDlpAuthenticationOptions
            {
                Username = Normalize(options.Username),
                Password = Normalize(options.Password),
                Twofactor = Normalize(options.TwoFactorCode),
                VideoPassword = Normalize(options.VideoPassword)
            },
            Workarounds = new YtDlpWorkaroundsOptions
            {
                NoCheckCertificates = options.SkipCertificateChecks,
                LegacyServerConnect = options.AllowLegacyConnections,
                AddHeaders = (options.ExtraHttpHeaders ?? [])
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToArray(),
                SleepRequests = Math.Max(3, options.SleepBetweenRequestsSeconds)
            }
        };

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ErrorCode(Exception ex)
        => ex switch
        {
            TimeoutException => "enrich_timeout",
            _ => "enrich_failed"
        };
}
