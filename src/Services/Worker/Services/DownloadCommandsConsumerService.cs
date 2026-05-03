using System.Buffers;
using System.IO.Hashing;
using System.Globalization;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using Shared.Storage;
using YtDlpSharpLib;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;

namespace Worker.Services;

/// <summary>
/// Worker-side JetStream consumer for the download flow's commands. The worker no longer
/// constructs storage paths or talks to DataBridge mid-stream — DataBridge does all routing
/// and dedupe, and the worker just executes the IO it's told to.
///
/// Consumer durables and the FROSTSTREAM_DOWNLOAD stream are provisioned by
/// <see cref="DownloadTopology"/>; both DataBridge and Worker register it, so whichever
/// service starts first creates them.
///
/// Result-event MessageIds are derived deterministically via
/// <see cref="DeterministicGuid.Create"/> so JetStream redelivery doesn't produce duplicate
/// downstream events.
/// </summary>
public sealed class DownloadCommandsConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    IYtDlpClient ytDlp,
    IBlobStorageProvider blobStorageProvider,
    IClock clock,
    ILogger<DownloadCommandsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Consume<FetchMetadataCommand>(DownloadTopology.WorkerFetchMetadataConsumer, HandleFetchMetadataAsync, stoppingToken),
            Consume<DownloadVideoCommand>(DownloadTopology.WorkerDownloadVideoConsumer, HandleDownloadVideoAsync, stoppingToken),
            Consume<UploadObjectCommand>(DownloadTopology.WorkerUploadObjectConsumer, HandleUploadObjectAsync, stoppingToken),
            Consume<DeleteTempFileCommand>(DownloadTopology.WorkerDeleteTempFileConsumer, HandleDeleteTempFileAsync, stoppingToken),
            Consume<DeleteUploadedObjectCommand>(DownloadTopology.WorkerDeleteUploadedObjectConsumer, HandleDeleteUploadedObjectAsync, stoppingToken),
        };

        logger.LogInformation("Subscribed to {Count} download command consumers on stream {Stream}.", consumers.Length, Stream.Value);
        return Task.WhenAll(consumers);
    }

    private Task Consume<TCommand>(
        string consumerName,
        Func<IJsMessageContext<TCommand>, Task> handler,
        CancellationToken stoppingToken)
        where TCommand : class, IFlowMessage
        => consumer.ConsumePullAsync<TCommand>(
            stream: Stream,
            consumer: ConsumerName.From(consumerName),
            handler: handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleFetchMetadataAsync(IJsMessageContext<FetchMetadataCommand> context)
    {
        var cmd = context.Message;

        try
        {
            var metadataResult = await ytDlp.TryGetVideoInfoAsync(cmd.SourceUrl);
            if (!metadataResult.Success || metadataResult.Data is not { } info)
            {
                throw new YtDlpProcessException(
                    $"yt-dlp metadata fetch failed for {cmd.SourceUrl}",
                    command: null,
                    exitCode: null,
                    lastStderrLines: metadataResult.ErrorOutput);
            }

            var provider = !string.IsNullOrWhiteSpace(info.Extractor)
                ? info.Extractor
                : info.ExtractorKey;
            var sourceMediaId = info.Id ?? info.DisplayId;
            var sourceLastModified = ResolveSourceLastModified(info);

            await Publish(DownloadSubjects.MetadataFetched, new MetadataFetched
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Provider = provider,
                SourceMediaId = sourceMediaId,
                SourceLastModified = sourceLastModified,
                Title = info.Title ?? info.FullTitle,
                Uploader = info.Uploader ?? info.Channel,
            });
            await context.AckAsync();
        }
        catch (YtDlpUnavailableException ex)
        {
            logger.LogWarning(ex,
                "FetchMetadata: source unavailable for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishMetadataFailedAsync(cmd, ex, FailureKind.Permanent);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "FetchMetadata failed for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishMetadataFailedAsync(cmd, ex, FailureKind.Transient);
            await context.AckAsync();
        }
    }

    private async Task HandleDownloadVideoAsync(IJsMessageContext<DownloadVideoCommand> context)
    {
        var cmd = context.Message;
        var tempDirectory = GetDownloadTempDirectory(cmd);
        string? tempFileRef = null;

        try
        {
            Directory.CreateDirectory(tempDirectory);

            await ytDlp.DownloadAsync(
                cmd.SourceUrl,
                tempDirectory,
                CreateDownloadOptions(),
                progress: null);

            tempFileRef = FindDownloadedVideoFile(tempDirectory)
                          ?? throw new InvalidOperationException("yt-dlp completed without producing a media file.");

            var fileInfo = new FileInfo(tempFileRef);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("yt-dlp completed but the temp file was not found.", tempFileRef);

            var contentHash = await ComputeXxHash128Async(tempFileRef);

            await Publish(DownloadSubjects.DownloadCompleted, new DownloadCompleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = tempFileRef,
                FileName = fileInfo.Name,
                FileSizeBytes = fileInfo.Length,
                ContentHashXxh128 = contentHash
            });
            await context.AckAsync();
        }
        catch (YtDlpUnavailableException ex)
        {
            logger.LogWarning(ex,
                "DownloadVideo: source unavailable for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishDownloadFailedAsync(cmd, ex, FailureKind.Permanent, tempFileRef ?? FindDownloadedVideoFile(tempDirectory));
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DownloadVideo failed for JobId {JobId} URL {SourceUrl}", cmd.JobId, cmd.SourceUrl);
            await PublishDownloadFailedAsync(cmd, ex, FailureKind.Transient, tempFileRef ?? FindDownloadedVideoFile(tempDirectory));
            await context.AckAsync();
        }
    }

    private async Task HandleUploadObjectAsync(IJsMessageContext<UploadObjectCommand> context)
    {
        var cmd = context.Message;

        try
        {
            var fileInfo = new FileInfo(cmd.TempFileRef);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Temp file to upload was not found.", cmd.TempFileRef);

            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey);

            await using (var stream = File.OpenRead(fileInfo.FullName))
            {
                await storage.WriteAsync(cmd.StoragePath, stream, append: false);
            }

            await Publish(DownloadSubjects.UploadCompleted, new UploadCompleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = cmd.TempFileRef,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = null,
                ContentHashXxh128 = cmd.ContentHashXxh128,
                ContentLengthBytes = fileInfo.Length
            });
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "UploadObject failed for JobId {JobId} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId, cmd.StorageKey, cmd.StoragePath);
            await PublishUploadFailedAsync(cmd, ex, UploadFailureKind(ex));
            await context.AckAsync();
        }
    }

    private async Task HandleDeleteTempFileAsync(IJsMessageContext<DeleteTempFileCommand> context)
    {
        var cmd = context.Message;

        try
        {
            DeleteTempFileRef(cmd.TempFileRef);

            await Publish(DownloadSubjects.TempFileDeleted, new TempFileDeleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = cmd.TempFileRef
            });
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DeleteTempFile failed for JobId {JobId} TempFileRef {TempFileRef}",
                cmd.JobId, cmd.TempFileRef);
            await Publish(DownloadSubjects.TempFileDeleteFailed, new TempFileDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                TempFileRef = cmd.TempFileRef,
                FailureKind = DeleteFailureKind(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
    }

    private async Task HandleDeleteUploadedObjectAsync(IJsMessageContext<DeleteUploadedObjectCommand> context)
    {
        var cmd = context.Message;

        try
        {
            if (string.IsNullOrWhiteSpace(cmd.StoragePath))
                throw new ArgumentException("Storage path is required.", nameof(cmd.StoragePath));

            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey);
            await storage.DeleteAsync([cmd.StoragePath]);

            await Publish(DownloadSubjects.UploadedObjectDeleted, new UploadedObjectDeleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = cmd.StorageVersion
            });
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DeleteUploadedObject failed for JobId {JobId} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId, cmd.StorageKey, cmd.StoragePath);
            await Publish(DownloadSubjects.UploadedObjectDeleteFailed, new UploadedObjectDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = cmd.StorageVersion,
                FailureKind = DeleteFailureKind(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private static Instant? ResolveSourceLastModified(VideoInfo info)
    {
        if (info.ModifiedTimestamp is { } modifiedTimestamp)
            return Instant.FromUnixTimeSeconds(modifiedTimestamp);

        if (string.IsNullOrWhiteSpace(info.ModifiedDate))
            return null;

        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(
            info.ModifiedDate,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var modifiedDate)
            ? Instant.FromDateTimeOffset(new DateTimeOffset(modifiedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null;
    }

    private Task PublishMetadataFailedAsync(FetchMetadataCommand cmd, Exception ex, FailureKind failureKind)
        => Publish(DownloadSubjects.MetadataFetchFailed, new MetadataFetchFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = failureKind,
            ErrorMessage = ex.Message
        });

    private Task PublishDownloadFailedAsync(
        DownloadVideoCommand cmd,
        Exception ex,
        FailureKind failureKind,
        string? tempFileRef)
        => Publish(DownloadSubjects.DownloadFailed, new DownloadFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = failureKind,
            ErrorCode = null,
            ErrorMessage = DescribeException(ex),
            TempFileRef = tempFileRef
        });

    private Task PublishUploadFailedAsync(UploadObjectCommand cmd, Exception ex, FailureKind failureKind)
        => Publish(DownloadSubjects.UploadFailed, new UploadFailed
        {
            JobId = cmd.JobId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.MessageId,
            MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
            OperationKey = $"{cmd.OperationKey}/failed",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = cmd.Attempt,
            FailureKind = failureKind,
            ErrorMessage = ex.Message,
            TempFileRef = cmd.TempFileRef
        });


    #region Helpers
    private static DownloadOptions CreateDownloadOptions()
        => new()
        {
            AbortOnError = true,
            OutputTemplate = "video.%(ext)s",
            OverwriteFiles = true,
            RestrictFilenames = true,
            YtDlp = new YtDlpOptions
            {
                VideoSelection = new YtDlpVideoSelectionOptions
                {
                    NoPlaylist = true
                },
                Filesystem = new YtDlpFilesystemOptions
                {
                    NoPart = true
                },
                PostProcessing = new YtDlpPostProcessingOptions
                {
                    FfmpegLocation = GetFfmpegLocation()
                },
                VerbositySimulation = new YtDlpVerbositySimulationOptions
                {
                    Newline = true
                }
            }
        };

    private static string GetDownloadTempDirectory(DownloadVideoCommand cmd)
        => Path.Combine(
            Path.GetTempPath(),
            "froststream",
            "downloads",
            cmd.JobId.ToString("N"),
            $"attempt-{cmd.Attempt.ToString(CultureInfo.InvariantCulture)}");

    private static string? GetFfmpegLocation()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        return Directory.Exists(toolsDirectory) ? toolsDirectory : null;
    }

    private static string? FindDownloadedVideoFile(string tempDirectory)
        => Directory.Exists(tempDirectory)
            ? Directory.EnumerateFiles(tempDirectory, "video.*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault()
            : null;

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

    private static void DeleteTempFileRef(string tempFileRef)
    {
        if (string.IsNullOrWhiteSpace(tempFileRef))
            throw new ArgumentException("Temp file ref is required.", nameof(tempFileRef));

        var fullPath = Path.GetFullPath(tempFileRef);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "froststream"));
        if (!IsWithinDirectory(fullPath, root))
            throw new ArgumentException("Temp file ref is outside the FrostStream temp directory.", nameof(tempFileRef));

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            DeleteEmptyTempParents(Path.GetDirectoryName(fullPath));
            return;
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
            DeleteEmptyTempParents(Path.GetDirectoryName(fullPath));
        }
    }

    private static void DeleteEmptyTempParents(string? startDirectory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "froststream", "downloads"));
        var current = string.IsNullOrWhiteSpace(startDirectory)
            ? null
            : Path.GetFullPath(startDirectory);

        while (current is not null
               && IsWithinDirectory(current, root)
               && !string.Equals(current, root, StringComparison.Ordinal))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
                return;

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "."
               && !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string DescribeException(Exception ex)
        => ex is YtDlpProcessException { LastStderrLines: { Length: > 0 } stderr }
            ? stderr
            : ex.Message;

    private static FailureKind UploadFailureKind(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException
            ? FailureKind.Permanent
            : FailureKind.Transient;

    private static FailureKind DeleteFailureKind(Exception ex)
        => ex is ArgumentException
            ? FailureKind.Permanent
            : FailureKind.Transient;
    #endregion
}
