using System.Buffers;
using System.IO.Hashing;
using System.Text.Json;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Imports;
using Shared.Messaging;
using Shared.Metadata;
using Worker.Metadata;
using YtDlpSharpLib.Models;

namespace Worker.Services;

public sealed class LocalImportCommandsConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    ITopologyManager topologyManager,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LocalImportCommandsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(LocalImportTopology.StreamNameValue);

    private static readonly JsonSerializerOptions ManifestJsonOptions = CreateManifestJsonOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        foreach (var tag in options.Tags)
        {
            await topologyManager.EnsureConsumerAsync(
                LocalImportTopology.TaggedWorkerConsumerSpec(
                    LocalImportTopology.WorkerReadLocalImportManifestConsumer,
                    LocalImportSubjects.ReadLocalImportManifestCommand,
                    tag),
                stoppingToken);
            await topologyManager.EnsureConsumerAsync(
                LocalImportTopology.TaggedWorkerConsumerSpec(
                    LocalImportTopology.WorkerPrepareLocalImportFileConsumer,
                    LocalImportSubjects.PrepareLocalImportFileCommand,
                    tag),
                stoppingToken);
            logger.LogInformation("Ensured tagged local import consumers for tag '{Tag}'.", tag);
        }

        var consumerTasks = new List<Task>();
        if (options.AcceptsUntaggedJobs || options.Tags.Count == 0)
        {
            consumerTasks.Add(Consume<ReadLocalImportManifestCommand>(
                LocalImportTopology.WorkerReadLocalImportManifestConsumer,
                HandleReadManifestAsync,
                stoppingToken));
            consumerTasks.Add(Consume<PrepareLocalImportFileCommand>(
                LocalImportTopology.WorkerPrepareLocalImportFileConsumer,
                HandlePrepareLocalImportFileAsync,
                stoppingToken));
        }

        foreach (var tag in options.Tags)
        {
            consumerTasks.Add(Consume<ReadLocalImportManifestCommand>(
                $"{LocalImportTopology.WorkerReadLocalImportManifestConsumer}-{tag}",
                HandleReadManifestAsync,
                stoppingToken));
            consumerTasks.Add(Consume<PrepareLocalImportFileCommand>(
                $"{LocalImportTopology.WorkerPrepareLocalImportFileConsumer}-{tag}",
                HandlePrepareLocalImportFileAsync,
                stoppingToken));
        }

        logger.LogInformation(
            "Subscribed to {Count} local import command consumer(s) on stream {Stream} (incoming root '{IncomingRoot}').",
            consumerTasks.Count,
            Stream.Value,
            options.IncomingRoot);

        await Task.WhenAll(consumerTasks);
    }

    private Task Consume<TCommand>(
        string consumerName,
        Func<IJsMessageContext<TCommand>, Task> handler,
        CancellationToken stoppingToken)
        where TCommand : class, IFlowMessage
        => consumer.ConsumePullAsync(
            stream: Stream,
            consumer: ConsumerName.From(consumerName),
            handler: handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleReadManifestAsync(IJsMessageContext<ReadLocalImportManifestCommand> context)
    {
        var cmd = context.Message;
        var incomingRoot = workerOptions.Value.IncomingRoot;
        var manifestPath = Path.Combine(incomingRoot, LocalImportIncoming.ManifestFileName);
        try
        {
            logger.LogInformation(
                "Reading local import manifest for BatchId {BatchId} from {ManifestPath}.",
                cmd.BatchId,
                manifestPath);

            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Local import manifest was not found.", manifestPath);

            LocalMediaImportManifest manifest;
            await using (var stream = File.OpenRead(manifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync<LocalMediaImportManifest>(stream, ManifestJsonOptions)
                           ?? throw new JsonException("Manifest body was empty.");
            }

            // Manifests authored without an explicit metadata block still ship a yt-dlp info.json
            // sidecar. Derive CapturedMediaMetadata from it here so DataBridge writes a
            // metadata.media_metadata row and the item surfaces in listings. Best-effort: a missing
            // or unparseable info.json degrades to importing the asset without rich metadata.
            manifest = await EnrichManifestMetadataAsync(manifest, incomingRoot, cmd.BatchId);

            await Publish(LocalImportSubjects.LocalImportManifestRead, new LocalImportManifestRead
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                BatchId = cmd.BatchId,
                Manifest = manifest
            });

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading local import manifest failed for BatchId {BatchId}.", cmd.BatchId);

            await Publish(LocalImportSubjects.LocalImportManifestReadFailed, new LocalImportManifestReadFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                BatchId = cmd.BatchId,
                ErrorCode = ManifestErrorCode(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
    }

    private async Task<LocalMediaImportManifest> EnrichManifestMetadataAsync(
        LocalMediaImportManifest manifest,
        string incomingRoot,
        Guid batchId)
    {
        if (manifest.Items.Count == 0)
            return manifest;

        var enrichedItems = new List<LocalMediaImportManifestItem>(manifest.Items.Count);
        var changed = false;
        foreach (var item in manifest.Items)
        {
            // Respect an explicitly authored metadata block; only fall back to the info.json sidecar.
            if (item.Metadata is not null || item.Sidecars?.InfoJson is not { } infoJsonPath)
            {
                enrichedItems.Add(item);
                continue;
            }

            var metadata = await TryMapInfoJsonMetadataAsync(incomingRoot, infoJsonPath, item.Provider, batchId);
            if (metadata is null)
            {
                enrichedItems.Add(item);
                continue;
            }

            enrichedItems.Add(item with { Metadata = metadata });
            changed = true;
        }

        return changed ? manifest with { Items = enrichedItems } : manifest;
    }

    private async Task<CapturedMediaMetadata?> TryMapInfoJsonMetadataAsync(
        string incomingRoot,
        string infoJsonRelativePath,
        string? provider,
        Guid batchId)
    {
        try
        {
            if (!LocalImportPathRules.TryResolveUnderAllowedRoots(
                    incomingRoot,
                    infoJsonRelativePath,
                    [incomingRoot],
                    out var fullPath,
                    out _,
                    out var error))
            {
                logger.LogWarning(
                    "Skipping info.json metadata for BatchId {BatchId}: {Error}. Importing without rich metadata.",
                    batchId,
                    error);
                return null;
            }

            if (!File.Exists(fullPath))
            {
                logger.LogWarning(
                    "info.json sidecar not found at {Path} for BatchId {BatchId}; importing without rich metadata.",
                    fullPath,
                    batchId);
                return null;
            }

            var json = await File.ReadAllTextAsync(fullPath);
            var info = JsonSerializer.Deserialize(json, YtDlpJsonContext.Default.VideoInfo);
            if (info is null)
            {
                logger.LogWarning(
                    "info.json at {Path} deserialized to null for BatchId {BatchId}; importing without rich metadata.",
                    fullPath,
                    batchId);
                return null;
            }

            var platform = FirstNonBlank(provider, info.Extractor, info.ExtractorKey) ?? "unknown";
            return YtDlpMetadataMapper.Map(info, platform, clock);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to derive metadata from info.json '{Path}' for BatchId {BatchId}; importing without rich metadata.",
                infoJsonRelativePath,
                batchId);
            return null;
        }
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private async Task HandlePrepareLocalImportFileAsync(IJsMessageContext<PrepareLocalImportFileCommand> context)
    {
        var cmd = context.Message;
        var incomingRoot = workerOptions.Value.IncomingRoot;
        try
        {
            logger.LogInformation(
                "Preparing local import file for BatchId {BatchId} ItemId {ItemId} File {File}.",
                cmd.BatchId,
                cmd.ItemId,
                cmd.File);

            var sourceFile = await PrepareFileAsync(incomingRoot, cmd.File);
            var infoJson = cmd.Sidecars?.InfoJson is { } infoJsonPath
                ? await PrepareFileAsync(incomingRoot, infoJsonPath)
                : null;
            var thumbnail = cmd.Sidecars?.Thumbnail is { } thumbnailPath
                ? await PrepareFileAsync(incomingRoot, thumbnailPath)
                : null;

            var captions = new List<LocalImportPreparedCaptionSidecar>();
            if (cmd.Sidecars?.Captions is { Count: > 0 } captionSpecs)
            {
                foreach (var caption in captionSpecs)
                {
                    var preparedCaption = await PrepareFileAsync(incomingRoot, caption.File);
                    captions.Add(new LocalImportPreparedCaptionSidecar
                    {
                        SourceFileRef = preparedCaption.SourceFileRef,
                        FileName = preparedCaption.FileName,
                        SizeBytes = preparedCaption.SizeBytes,
                        ContentHashXxh128 = preparedCaption.ContentHashXxh128,
                        LanguageCode = caption.LanguageCode,
                        CaptionType = caption.CaptionType,
                        Name = caption.Name
                    });
                }
            }

            await Publish(LocalImportSubjects.LocalImportFilePrepared, new LocalImportFilePrepared
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                BatchId = cmd.BatchId,
                ItemId = cmd.ItemId,
                SourceFileRef = sourceFile.SourceFileRef,
                FileName = sourceFile.FileName,
                FileSizeBytes = sourceFile.SizeBytes,
                ContentHashXxh128 = sourceFile.ContentHashXxh128,
                InfoJson = infoJson,
                Thumbnail = thumbnail,
                Captions = captions
            });

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Preparing local import file failed for BatchId {BatchId} ItemId {ItemId} File {File}.",
                cmd.BatchId,
                cmd.ItemId,
                cmd.File);

            await Publish(LocalImportSubjects.LocalImportFilePrepareFailed, new LocalImportFilePrepareFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                BatchId = cmd.BatchId,
                ItemId = cmd.ItemId,
                FailureKind = FailureKind.Permanent,
                ErrorCode = ErrorCode(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
    }

    private static async Task<LocalImportPreparedSidecar> PrepareFileAsync(string incomingRoot, string relativePath)
    {
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

        var file = new FileInfo(fullPath);
        if (Directory.Exists(fullPath))
            throw new IOException("Local import path is a directory.");
        if (!file.Exists)
            throw new FileNotFoundException("Local import file was not found.", fullPath);

        return new LocalImportPreparedSidecar
        {
            SourceFileRef = file.FullName,
            FileName = file.Name,
            SizeBytes = file.Length,
            ContentHashXxh128 = await ComputeXxHash128Async(file.FullName)
        };
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private static async Task<string> ComputeXxHash128Async(string path)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);

        try
        {
            await using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            Span<byte> hash = stackalloc byte[16];
            hasher.GetCurrentHash(hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ManifestErrorCode(Exception ex)
        => ex switch
        {
            FileNotFoundException => "manifest_missing",
            DirectoryNotFoundException => "manifest_missing",
            JsonException => "manifest_invalid",
            UnauthorizedAccessException => "manifest_access_denied",
            _ => "manifest_read_failed"
        };

    private static string ErrorCode(Exception ex)
        => ex switch
        {
            FileNotFoundException => "source_missing",
            DirectoryNotFoundException => "source_missing",
            ArgumentException => "invalid_source_path",
            UnauthorizedAccessException => "source_access_denied",
            IOException => "invalid_source_file",
            _ => "prepare_failed"
        };

    private static JsonSerializerOptions CreateManifestJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }
}
