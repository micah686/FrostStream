using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Imports;
using Shared.Messaging;

namespace Worker.Services;

public sealed partial class LocalImportScanConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    ITopologyManager topologyManager,
    Func<string, IObjectStore> objectStoreFactory,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LocalImportScanConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(LocalImportTopology.StreamNameValue);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IngestTimeout = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".avi", ".flv", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".ogg", ".ogv", ".ts", ".webm", ".wmv"
    };
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };
    private static readonly HashSet<string> CaptionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".vtt", ".ass", ".ssa", ".ttml"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        foreach (var tag in options.Tags)
        {
            await topologyManager.EnsureConsumerAsync(
                LocalImportTopology.TaggedWorkerConsumerSpec(
                    LocalImportTopology.WorkerScanLocalImportSourceConsumer,
                    LocalImportSubjects.ScanLocalImportSourceCommand,
                    tag),
                stoppingToken);
        }

        var tasks = new List<Task>();
        if (options.AcceptsUntaggedJobs || options.Tags.Count == 0)
        {
            tasks.Add(Consume(LocalImportTopology.WorkerScanLocalImportSourceConsumer, stoppingToken));
        }

        foreach (var tag in options.Tags)
        {
            tasks.Add(Consume($"{LocalImportTopology.WorkerScanLocalImportSourceConsumer}-{tag}", stoppingToken));
        }

        logger.LogInformation("Subscribed to {Count} local import scan consumer(s).", tasks.Count);
        await Task.WhenAll(tasks);
    }

    private Task Consume(string consumerName, CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<ScanLocalImportSourceCommand>(
            Stream,
            ConsumerName.From(consumerName),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<ScanLocalImportSourceCommand> context)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var heartbeatTask = JetStreamHeartbeat.RunAsync(context, HeartbeatInterval, logger, "Local import scan", heartbeatCts.Token);
        try
        {
            await ScanAsync(context.Message, heartbeatCts.Token);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed scanning local import session {SessionId}; marking scan failed.", context.Message.SessionId);
            try
            {
                await messageBus.RequestAsync<ImportSessionScanFailedRequest, ImportSessionScanFailedResponse>(
                    ImportSessionSubjects.ScanFailed,
                    new ImportSessionScanFailedRequest
                    {
                        SessionId = context.Message.SessionId,
                        ErrorMessage = ex.Message
                    },
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception markEx)
            {
                logger.LogWarning(markEx, "Failed notifying DataBridge of local import scan failure for {SessionId}.", context.Message.SessionId);
            }
            await context.AckAsync();
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { }
        }
    }

    private async Task ScanAsync(ScanLocalImportSourceCommand command, CancellationToken cancellationToken)
    {
        if (command.SourceKind != ImportSessionSourceKind.WorkerIncoming)
            throw new NotSupportedException("Only worker incoming scans are supported.");

        var incomingRoot = workerOptions.Value.IncomingRoot;
        var scanRoot = ResolveScanRoot(incomingRoot, command.SubPath);
        var objectStore = objectStoreFactory(LocalImportTopology.ManifestObjectStoreBucket);
        var objectKey = BuildObjectKey(command.SessionId, clock.GetCurrentInstant());
        var pipe = new Pipe();

        logger.LogInformation(
            "Scanning local import session {SessionId} under {ScanRoot}; staging object {ObjectKey}.",
            command.SessionId,
            scanRoot,
            objectKey);

        var putTask = objectStore.PutAsync(objectKey, pipe.Reader.AsStream(), cancellationToken);
        int count;
        try
        {
            Exception? writeException = null;
            try
            {
                count = await WriteScanAsync(incomingRoot, scanRoot, pipe.Writer.AsStream(leaveOpen: true), cancellationToken);
            }
            catch (Exception ex)
            {
                writeException = ex;
                throw;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(writeException);
            }

            await putTask;
        }
        catch
        {
            try { await putTask; } catch { }
            throw;
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
        }

        var response = await messageBus.RequestAsync<ImportSessionScanIngestRequest, ImportSessionScanIngestResponse>(
            ImportSessionSubjects.ScanIngest,
            new ImportSessionScanIngestRequest
            {
                SessionId = command.SessionId,
                ObjectBucket = LocalImportTopology.ManifestObjectStoreBucket,
                ObjectKey = objectKey,
                ItemCount = count
            },
            IngestTimeout,
            cancellationToken);

        if (response is not { Success: true })
            throw new InvalidOperationException(response?.ErrorMessage ?? "Import session scan ingest failed.");

        logger.LogInformation("Scanned {Count} file(s) for local import session {SessionId}.", count, command.SessionId);
    }

    private async Task<int> WriteScanAsync(
        string incomingRoot,
        string scanRoot,
        Stream target,
        CancellationToken cancellationToken)
    {
        var count = 0;
        await using var writer = new StreamWriter(target, leaveOpen: true);
        foreach (var file in Directory.EnumerateFiles(scanRoot, "*", SearchOption.AllDirectories)
                     .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await BuildItemAsync(incomingRoot, file, cancellationToken);
            await writer.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions).AsMemory(), cancellationToken);
            count++;
        }

        await writer.FlushAsync(cancellationToken);
        return count;
    }

    private async Task<ImportSessionScannedItem> BuildItemAsync(
        string incomingRoot,
        string file,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        var relativePath = ToRelativePath(incomingRoot, file);
        var sidecars = FindSidecars(file, incomingRoot);
        var infoMetadata = sidecars.InfoJson is null
            ? null
            : await TryReadInfoJsonAsync(Path.Combine(incomingRoot, FromRelativePath(sidecars.InfoJson)), cancellationToken);
        var nfoMetadata = sidecars.Nfo is null
            ? null
            : await TryReadNfoAsync(Path.Combine(incomingRoot, FromRelativePath(sidecars.Nfo)), cancellationToken);
        var title = FirstNonBlank(infoMetadata?.Title, nfoMetadata?.Title, CleanTitle(Path.GetFileNameWithoutExtension(file)));
        var provider = FirstNonBlank(infoMetadata?.Provider, nfoMetadata?.Provider);
        var sourceMediaId = FirstNonBlank(infoMetadata?.SourceMediaId, nfoMetadata?.SourceMediaId);
        var sourceUrl = FirstNonBlank(infoMetadata?.SourceUrl, nfoMetadata?.SourceUrl);
        var hasExternalMetadata = infoMetadata is not null || nfoMetadata is not null;
        var scanMetadata = new
        {
            filename = new { title },
            infoJson = infoMetadata,
            nfo = nfoMetadata
        };

        return new ImportSessionScannedItem
        {
            RelativePath = relativePath,
            FileName = info.Name,
            FileSizeBytes = info.Length,
            FileMtime = Instant.FromDateTimeUtc(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
            SidecarsJson = JsonSerializer.Serialize(sidecars, JsonOptions),
            Provider = provider,
            SourceMediaId = sourceMediaId,
            SourceUrl = sourceUrl,
            Title = title,
            ScanMetadataJson = JsonSerializer.Serialize(scanMetadata, JsonOptions),
            MetadataState = hasExternalMetadata
                ? ImportSessionItemMetadataState.Ready
                : ImportSessionItemMetadataState.Incomplete
        };
    }

    private static LocalImportScanSidecars FindSidecars(string mediaPath, string incomingRoot)
    {
        var directory = Path.GetDirectoryName(mediaPath) ?? incomingRoot;
        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath));
        string? infoJson = null;
        foreach (var candidate in new[] { $"{stem}.info.json", $"{stem}.json" })
        {
            if (File.Exists(candidate))
            {
                infoJson = ToRelativePath(incomingRoot, candidate);
                break;
            }
        }

        string? nfo = null;
        var nfoCandidate = $"{stem}.nfo";
        if (File.Exists(nfoCandidate))
            nfo = ToRelativePath(incomingRoot, nfoCandidate);

        string? thumbnail = null;
        foreach (var extension in ThumbnailExtensions)
        {
            var candidate = $"{stem}{extension}";
            if (File.Exists(candidate))
            {
                thumbnail = ToRelativePath(incomingRoot, candidate);
                break;
            }
        }

        var captions = Directory.EnumerateFiles(directory, $"{Path.GetFileNameWithoutExtension(mediaPath)}.*")
            .Where(path => CaptionExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new LocalImportScanCaptionSidecar
            {
                File = ToRelativePath(incomingRoot, path),
                LanguageCode = TryInferLanguageCode(mediaPath, path),
                CaptionType = "subtitle"
            })
            .ToList();

        return new LocalImportScanSidecars
        {
            InfoJson = infoJson,
            Nfo = nfo,
            Thumbnail = thumbnail,
            Captions = captions
        };
    }

    private static async Task<ScannedMetadata?> TryReadInfoJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            return new ScannedMetadata
            {
                Title = ReadString(root, "title", "fulltitle"),
                Provider = ReadString(root, "extractor", "extractor_key", "ie_key"),
                SourceMediaId = ReadString(root, "id", "display_id"),
                SourceUrl = ReadString(root, "webpage_url", "original_url", "url"),
                Description = ReadString(root, "description")
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ScannedMetadata?> TryReadNfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            var root = doc.Root;
            if (root is null)
                return null;

            return new ScannedMetadata
            {
                Title = ElementValue(root, "title"),
                Provider = ElementValue(root, "studio"),
                SourceMediaId = ElementValue(root, "uniqueid"),
                SourceUrl = ElementValue(root, "website", "trailer"),
                Description = ElementValue(root, "plot", "outline")
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveScanRoot(string incomingRoot, string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath))
            return incomingRoot;

        if (!LocalImportPathRules.TryResolveUnderAllowedRoots(
                incomingRoot,
                subPath,
                [incomingRoot],
                out var fullPath,
                out _,
                out var error))
        {
            throw new ArgumentException(error, nameof(subPath));
        }

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Scan sub-path was not found: {subPath}");

        return fullPath;
    }

    private static string ToRelativePath(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FromRelativePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar);

    private static string BuildObjectKey(Guid sessionId, Instant now)
        => $"sessions/{sessionId:N}/{now.ToDateTimeOffset():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ndjson";

    private static string CleanTitle(string value)
        => TitleCleanupRegex()
            .Replace(value.Replace('_', ' ').Replace('.', ' '), " ")
            .Trim();

    private static string? TryInferLanguageCode(string mediaPath, string captionPath)
    {
        var mediaStem = Path.GetFileNameWithoutExtension(mediaPath);
        var captionName = Path.GetFileNameWithoutExtension(captionPath);
        if (!captionName.StartsWith(mediaStem, StringComparison.OrdinalIgnoreCase))
            return null;

        var suffix = captionName[mediaStem.Length..].Trim('.', '-', '_');
        return suffix.Length is >= 2 and <= 12 ? suffix : null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
                return Normalize(element.GetString());
        }

        return null;
    }

    private static string? ElementValue(XElement root, params string[] names)
    {
        foreach (var name in names)
        {
            var element = root.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var value = Normalize(element?.Value);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.Select(Normalize).FirstOrDefault(x => x is not null);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex TitleCleanupRegex();

    private sealed record ScannedMetadata
    {
        public string? Title { get; init; }
        public string? Provider { get; init; }
        public string? SourceMediaId { get; init; }
        public string? SourceUrl { get; init; }
        public string? Description { get; init; }
    }

    private sealed record LocalImportScanSidecars
    {
        public string? InfoJson { get; init; }
        public string? Nfo { get; init; }
        public string? Thumbnail { get; init; }
        public IReadOnlyList<LocalImportScanCaptionSidecar> Captions { get; init; } = [];
    }

    private sealed record LocalImportScanCaptionSidecar
    {
        public required string File { get; init; }
        public string? LanguageCode { get; init; }
        public string? CaptionType { get; init; }
    }
}
