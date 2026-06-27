using System.Buffers;
using System.IO.Hashing;
using System.Globalization;
using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Messaging;
using Shared.Metadata;
using Shared.Secrets;
using Shared.Storage;
using Worker.Metadata;
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
    ITopologyManager topologyManager,
    IYtDlpClient ytDlp,
    IBlobStorageProvider blobStorageProvider,
    ISecretStore secretStore,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    ILogger<DownloadCommandsConsumerService> logger) : BackgroundService
{
    private const string MediaFileBase = "media";
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);
    private static readonly TimeSpan StorageProbeTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Remove any cookie scratch dirs left behind by a previous crash before serving traffic.
        SweepCookieScratchRoot();

        var options = workerOptions.Value;
        var tags = options.Tags;

        // Ensure per-tag consumers exist in JetStream before subscribing. EnsureConsumerAsync is
        // idempotent — multiple worker instances with the same tags race to create the same durable
        // consumers, and the second+ calls become no-ops.
        foreach (var tag in tags)
        {
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerFetchMetadataConsumer,        DownloadSubjects.FetchMetadataCommand,        tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerDownloadVideoConsumer,        DownloadSubjects.DownloadVideoCommand,        tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerUploadObjectConsumer,         DownloadSubjects.UploadObjectCommand,         tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerDeleteTempFileConsumer,       DownloadSubjects.DeleteTempFileCommand,       tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerDeleteUploadedObjectConsumer, DownloadSubjects.DeleteUploadedObjectCommand, tag), stoppingToken);
            logger.LogInformation("Ensured tagged download consumers for tag '{Tag}'.", tag);
        }

        var consumerTasks = new List<Task>();

        // Subscribe to untagged consumers when this worker accepts jobs with no required tag.
        if (options.AcceptsUntaggedJobs || tags.Count == 0)
        {
            consumerTasks.Add(Consume<FetchMetadataCommand>(DownloadTopology.WorkerFetchMetadataConsumer,        HandleFetchMetadataAsync,        stoppingToken));
            consumerTasks.Add(Consume<DownloadVideoCommand>(DownloadTopology.WorkerDownloadVideoConsumer,        HandleDownloadVideoAsync,        stoppingToken));
            consumerTasks.Add(Consume<UploadObjectCommand>(DownloadTopology.WorkerUploadObjectConsumer,          HandleUploadObjectAsync,         stoppingToken));
            consumerTasks.Add(Consume<DeleteTempFileCommand>(DownloadTopology.WorkerDeleteTempFileConsumer,      HandleDeleteTempFileAsync,       stoppingToken));
            consumerTasks.Add(Consume<DeleteUploadedObjectCommand>(DownloadTopology.WorkerDeleteUploadedObjectConsumer, HandleDeleteUploadedObjectAsync, stoppingToken));
        }

        // Subscribe to per-tag consumers.
        foreach (var tag in tags)
        {
            consumerTasks.Add(Consume<FetchMetadataCommand>($"{DownloadTopology.WorkerFetchMetadataConsumer}-{tag}",        HandleFetchMetadataAsync,        stoppingToken));
            consumerTasks.Add(Consume<DownloadVideoCommand>($"{DownloadTopology.WorkerDownloadVideoConsumer}-{tag}",        HandleDownloadVideoAsync,        stoppingToken));
            consumerTasks.Add(Consume<UploadObjectCommand>($"{DownloadTopology.WorkerUploadObjectConsumer}-{tag}",          HandleUploadObjectAsync,         stoppingToken));
            consumerTasks.Add(Consume<DeleteTempFileCommand>($"{DownloadTopology.WorkerDeleteTempFileConsumer}-{tag}",      HandleDeleteTempFileAsync,       stoppingToken));
            consumerTasks.Add(Consume<DeleteUploadedObjectCommand>($"{DownloadTopology.WorkerDeleteUploadedObjectConsumer}-{tag}", HandleDeleteUploadedObjectAsync, stoppingToken));
        }

        logger.LogInformation(
            "Subscribed to {Count} download command consumers on stream {Stream}. Tags: [{Tags}] AcceptsUntaggedJobs: {AcceptsUntaggedJobs}",
            consumerTasks.Count,
            Stream.Value,
            string.Join(", ", tags),
            options.AcceptsUntaggedJobs);

        await Task.WhenAll(consumerTasks);
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
        var cookieScratch = GetCookieScratchDirectory(cmd.JobId);

        try
        {
            logger.LogInformation(
                "Metadata fetch started for JobId {JobId} Attempt {Attempt} URL {SourceUrl} StorageKey {StorageKey} WorkerTag {WorkerTag} HasCookieProfile {HasCookieProfile}",
                cmd.JobId,
                cmd.Attempt,
                cmd.SourceUrl,
                cmd.StorageKey,
                cmd.RequiredWorkerTag,
                !string.IsNullOrWhiteSpace(cmd.CookieSecretPath));

            // Probe storage connectivity before invoking yt-dlp. A failed probe fails the job
            // immediately with a permanent failure so the saga doesn't waste time downloading
            // bytes that can never be uploaded.
            if (!await ProbeStorageAsync(cmd))
            {
                await context.AckAsync();
                return;
            }

            await using var cookies = await CookieMaterializer.CreateFromPathAsync(
                secretStore,
                cmd.CookieSecretPath,
                cookieScratch,
                logger);
            var metadataOptions = YtDlpOptionsMerger.Merge(
                cmd.YtDlpOptions,
                ffmpegLocation: GetFfmpegLocation(),
                cookieFilePath: cookies.FilePath);

            var metadataResult = await ytDlp.TryGetVideoInfoAsync(cmd.SourceUrl, overrideOptions: metadataOptions);
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

            PlaceholderContentDetector.ThrowIfPlaceholderMetadata(info, provider);

            CapturedMediaMetadata? richMetadata;
            try
            {
                richMetadata = YtDlpMetadataMapper.Map(info, provider ?? "", clock);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Metadata mapping failed for JobId {JobId} Provider {Provider} SourceMediaId {SourceMediaId}",
                    cmd.JobId,
                    provider,
                    sourceMediaId);
                throw;
            }

            logger.LogInformation(
                "Metadata fetch completed for JobId {JobId} Attempt {Attempt} Provider {Provider} SourceMediaId {SourceMediaId} Title {Title}",
                cmd.JobId,
                cmd.Attempt,
                provider,
                sourceMediaId,
                info.Title ?? info.FullTitle);


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
                RichMetadata = richMetadata
            });
            await context.AckAsync();
        }
        catch (YtDlpUnavailableException ex)
        {
            logger.LogWarning(ex,
                "FetchMetadata: source unavailable for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishMetadataFailedAsync(cmd, ex, YtDlpFailureDetails.ClassifyYtDlpFailure(ex));
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "FetchMetadata failed for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishMetadataFailedAsync(cmd, ex, YtDlpFailureDetails.ClassifyFailure(ex));
            await context.AckAsync();
        }
        finally
        {
            DeleteCookieScratch(cookieScratch);
        }
    }

    private async Task HandleDownloadVideoAsync(IJsMessageContext<DownloadVideoCommand> context)
    {
        var cmd = context.Message;
        var tempDirectory = GetDownloadTempDirectory(cmd);
        var cookieScratch = GetCookieScratchDirectory(cmd.JobId);
        string? tempFileRef = null;
        DownloadProgressReporter? progress = null;

        try
        {
            Directory.CreateDirectory(tempDirectory);

            logger.LogInformation(
                "Download started for JobId {JobId} Attempt {Attempt} URL {SourceUrl} MediaKind {MediaKind} HasCookieProfile {HasCookieProfile} TempDirectory {TempDirectory}",
                cmd.JobId,
                cmd.Attempt,
                cmd.SourceUrl,
                cmd.MediaKind,
                !string.IsNullOrWhiteSpace(cmd.CookieSecretPath),
                tempDirectory);

            await using var cookies = await CookieMaterializer.CreateFromPathAsync(
                secretStore,
                cmd.CookieSecretPath,
                cookieScratch,
                logger);

            progress = new DownloadProgressReporter(cmd, publisher, clock, logger);
            await DispatchYtDlpAsync(cmd, tempDirectory, cookies.FilePath, progress);
            await progress.FlushAsync();

            tempFileRef = FindDownloadedMediaFile(tempDirectory)
                          ?? throw new InvalidOperationException("yt-dlp completed without producing a media file.");

            var fileInfo = new FileInfo(tempFileRef);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("yt-dlp completed but the temp file was not found.", tempFileRef);

            var contentHash = await ComputeXxHash128Async(tempFileRef);
            PlaceholderContentDetector.ThrowIfPlaceholderContentHash(contentHash);

            var infoJson = await ResolveInfoJsonSidecarAsync(tempDirectory);
            var (thumbnail, captions) = await ResolveAssetSidecarsAsync(tempDirectory, tempFileRef);

            logger.LogInformation(
                "Download completed for JobId {JobId} Attempt {Attempt} File {TempFileRef} SizeBytes {FileSizeBytes} ContentHash {ContentHashXxh128} InfoJson {InfoJsonFileName} Thumbnail {ThumbnailFileName} Captions {CaptionCount}",
                cmd.JobId,
                cmd.Attempt,
                tempFileRef,
                fileInfo.Length,
                contentHash,
                infoJson?.FileName,
                thumbnail?.FileName,
                captions.Count);

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
                ContentHashXxh128 = contentHash,
                InfoJsonTempFileRef = infoJson?.TempFileRef,
                InfoJsonFileName = infoJson?.FileName,
                InfoJsonSizeBytes = infoJson?.SizeBytes,
                InfoJsonContentHashXxh128 = infoJson?.ContentHash,
                Thumbnail = thumbnail,
                Captions = captions
            });
            await context.AckAsync();
        }
        catch (YtDlpUnavailableException ex)
        {
            if (progress is not null)
                await progress.FlushAsync();

            logger.LogWarning(ex,
                "DownloadVideo: source unavailable for JobId {JobId} URL {SourceUrl}",
                cmd.JobId, cmd.SourceUrl);
            await PublishDownloadFailedAsync(cmd, ex, YtDlpFailureDetails.ClassifyYtDlpFailure(ex), tempFileRef ?? FindDownloadedMediaFile(tempDirectory));
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            if (progress is not null)
                await progress.FlushAsync();

            logger.LogError(ex, "DownloadVideo failed for JobId {JobId} URL {SourceUrl}", cmd.JobId, cmd.SourceUrl);
            await PublishDownloadFailedAsync(cmd, ex, YtDlpFailureDetails.ClassifyFailure(ex), tempFileRef ?? FindDownloadedMediaFile(tempDirectory));
            await context.AckAsync();
        }
        finally
        {
            DeleteCookieScratch(cookieScratch);
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

            logger.LogInformation(
                "Upload started for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef} SizeBytes {FileSizeBytes} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId,
                cmd.Attempt,
                cmd.TempFileRef,
                fileInfo.Length,
                cmd.StorageKey,
                cmd.StoragePath);

            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey);

            await using (var stream = File.OpenRead(fileInfo.FullName))
            {
                await storage.WriteAsync(cmd.StoragePath, stream, append: false);
            }

            logger.LogInformation(
                "Upload completed for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {FileSizeBytes} ContentHash {ContentHashXxh128}",
                cmd.JobId,
                cmd.Attempt,
                cmd.StorageKey,
                cmd.StoragePath,
                fileInfo.Length,
                cmd.ContentHashXxh128);

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
                ContentLengthBytes = fileInfo.Length,
                Kind = cmd.Kind
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
            logger.LogInformation(
                "Temp file cleanup started for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef}",
                cmd.JobId,
                cmd.Attempt,
                cmd.TempFileRef);

            DeleteTempFileRef(cmd.TempFileRef);

            logger.LogInformation(
                "Temp file cleanup completed for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef}",
                cmd.JobId,
                cmd.Attempt,
                cmd.TempFileRef);

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

            logger.LogInformation(
                "Uploaded object cleanup started for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId,
                cmd.Attempt,
                cmd.StorageKey,
                cmd.StoragePath);

            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey);
            await storage.DeleteAsync([cmd.StoragePath]);

            logger.LogInformation(
                "Uploaded object cleanup completed for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId,
                cmd.Attempt,
                cmd.StorageKey,
                cmd.StoragePath);

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

    /// <summary>
    /// Probes the target storage backend for connectivity. Returns <see langword="true"/> when the
    /// backend is reachable; publishes <see cref="MetadataFetchFailed"/> and returns
    /// <see langword="false"/> when it is not. Failing fast here prevents a worker from spending
    /// time on a multi-minute yt-dlp download only to discover at upload time that its storage
    /// backend is unreachable (e.g. a NAS worker tag pointing at a locally-mounted share that
    /// went offline).
    /// </summary>
    private async Task<bool> ProbeStorageAsync(FetchMetadataCommand cmd)
    {
        try
        {
            using var cts = new CancellationTokenSource(StorageProbeTimeout);
            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey, cts.Token);
            await storage.ListAsync(new ListOptions { MaxResults = 1 }, cts.Token);

            logger.LogDebug(
                "Storage probe succeeded for JobId {JobId} StorageKey {StorageKey}.",
                cmd.JobId,
                cmd.StorageKey);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Storage probe failed for JobId {JobId} StorageKey {StorageKey}; failing job permanently.",
                cmd.JobId,
                cmd.StorageKey);

            await Publish(DownloadSubjects.MetadataFetchFailed, new MetadataFetchFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/storage-probe-failed"),
                OperationKey = $"{cmd.OperationKey}/storage-probe-failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                FailureKind = FailureKind.Permanent,
                ErrorCode = "storage_unavailable",
                ErrorMessage = $"Storage backend '{cmd.StorageKey}' is unreachable from this worker: {ex.Message}"
            });
            return false;
        }
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private Task PublishFailureAsync<TCommand, TFailure>(
        string subject,
        TCommand command,
        Func<FailureEnvelope, TFailure> factory)
        where TCommand : IFlowMessage
        where TFailure : IFlowMessage
        => Publish(subject, factory(FailureEnvelope.From(command, clock)));

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
        => PublishFailureAsync(DownloadSubjects.MetadataFetchFailed, cmd, envelope => new MetadataFetchFailed
        {
            JobId = envelope.JobId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            MessageId = envelope.MessageId,
            OperationKey = envelope.OperationKey,
            OccurredAt = envelope.OccurredAt,
            Attempt = envelope.Attempt,
            FailureKind = failureKind,
            ErrorCode = YtDlpFailureDetails.ErrorCode(ex),
            ErrorMessage = YtDlpFailureDetails.DescribeException(ex)
        });

    private Task PublishDownloadFailedAsync(
        DownloadVideoCommand cmd,
        Exception ex,
        FailureKind failureKind,
        string? tempFileRef)
        => PublishFailureAsync(DownloadSubjects.DownloadFailed, cmd, envelope => new DownloadFailed
        {
            JobId = envelope.JobId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            MessageId = envelope.MessageId,
            OperationKey = envelope.OperationKey,
            OccurredAt = envelope.OccurredAt,
            Attempt = envelope.Attempt,
            FailureKind = failureKind,
            ErrorCode = YtDlpFailureDetails.ErrorCode(ex),
            ErrorMessage = YtDlpFailureDetails.DescribeException(ex),
            TempFileRef = tempFileRef
        });

    private Task PublishUploadFailedAsync(UploadObjectCommand cmd, Exception ex, FailureKind failureKind)
        => PublishFailureAsync(DownloadSubjects.UploadFailed, cmd, envelope => new UploadFailed
        {
            JobId = envelope.JobId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            MessageId = envelope.MessageId,
            OperationKey = envelope.OperationKey,
            OccurredAt = envelope.OccurredAt,
            Attempt = envelope.Attempt,
            FailureKind = failureKind,
            ErrorMessage = ex.Message,
            TempFileRef = cmd.TempFileRef,
            Kind = cmd.Kind
        });

    private readonly record struct FailureEnvelope(
        Guid JobId,
        Guid CorrelationId,
        Guid? CausationId,
        Guid MessageId,
        string OperationKey,
        Instant OccurredAt,
        int Attempt)
    {
        public static FailureEnvelope From(IFlowMessage command, IClock clock)
            => new(
                command.JobId,
                command.CorrelationId,
                command.MessageId,
                DeterministicGuid.Create(command.MessageId, "/failed"),
                $"{command.OperationKey}/failed",
                clock.GetCurrentInstant(),
                command.Attempt);
    }


    #region Helpers
    private Task DispatchYtDlpAsync(
        DownloadVideoCommand cmd,
        string tempDirectory,
        string? cookieFilePath,
        DownloadProgressReporter progress)
    {
        var ytDlpOptions = ApplyOperationalDefaults(YtDlpOptionsMerger.Merge(
            cmd.YtDlpOptions,
            ffmpegLocation: GetFfmpegLocation(),
            cookieFilePath: cookieFilePath));

        var outputTemplate = $"{MediaFileBase}.%(ext)s";

        if (cmd.MediaKind == MediaKind.Audio)
        {
            return ytDlp.DownloadAudioAsync(
                cmd.SourceUrl,
                tempDirectory,
                new AudioDownloadOptions
                {
                    AbortOnError = true,
                    OutputTemplate = outputTemplate,
                    OverwriteFiles = true,
                    RestrictFilenames = true,
                    AudioFormat = cmd.AudioFormat ?? AudioConversionFormat.M4a,
                    YtDlp = ytDlpOptions
                },
                progress);
        }

        return ytDlp.DownloadAsync(
            cmd.SourceUrl,
            tempDirectory,
            new DownloadOptions
            {
                AbortOnError = true,
                OutputTemplate = outputTemplate,
                OverwriteFiles = true,
                RestrictFilenames = true,
                YtDlp = ytDlpOptions
            },
            progress);
    }

    /// <summary>
    /// Layer Worker-mandated defaults on top of the merged options. We force
    /// <c>NoPlaylist</c>, <c>NoPart</c>, and <c>Newline</c> regardless of caller input
    /// because they're load-bearing for the saga (one file per job, atomic temp moves,
    /// readable progress).
    ///
    /// Per-media asset capture: the thumbnail is written by default (callers can opt out with
    /// <c>NoWriteThumbnail</c>); subtitle capture stays opt-in — the caller drives it via
    /// <c>WriteSubs</c>/<c>WriteAutoSubs</c> + <c>SubLangs</c>, which yt-dlp already honors.
    /// The downloaded sidecar files are collected post-download and uploaded as durable blobs.
    /// </summary>
    private static YtDlpOptions ApplyOperationalDefaults(YtDlpOptions options)
        => options with
        {
            VideoSelection = options.VideoSelection with { NoPlaylist = true },
            Filesystem = options.Filesystem with { NoPart = true },
            VerbositySimulation = options.VerbositySimulation with { Newline = true },
            Thumbnail = options.Thumbnail with { WriteThumbnail = !options.Thumbnail.NoWriteThumbnail }
        };

    private static string GetDownloadTempDirectory(DownloadVideoCommand cmd)
        => Path.Combine(
            Path.GetTempPath(),
            "froststream",
            "downloads",
            cmd.JobId.ToString("N"),
            $"attempt-{cmd.Attempt.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Root for per-job cookie scratch dirs. Prefers tmpfs (<c>/dev/shm</c>) on Linux so cookie bytes
    /// live in RAM and never touch persistent disk; falls back to the system temp dir elsewhere. The
    /// materialized file is deleted immediately after each yt-dlp run regardless.
    /// </summary>
    private static string GetCookieScratchRoot()
        => OperatingSystem.IsLinux() && Directory.Exists("/dev/shm")
            ? Path.Combine("/dev/shm", "froststream", "cookies")
            : Path.Combine(Path.GetTempPath(), "froststream", "cookies");

    private static string GetCookieScratchDirectory(Guid jobId)
        => Path.Combine(GetCookieScratchRoot(), jobId.ToString("N"));

    /// <summary>Best-effort removal of a per-job cookie scratch dir once its file is gone. Leftovers
    /// are harmless and swept on next startup, so failures here are ignored.</summary>
    private void DeleteCookieScratch(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete cookie scratch directory {Directory}.", directory);
        }
    }

    /// <summary>Clears any cookie scratch dirs orphaned by a previous crash, so no materialized cookie
    /// file outlives the process that wrote it.</summary>
    private void SweepCookieScratchRoot()
    {
        try
        {
            var root = GetCookieScratchRoot();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not sweep the cookie scratch root on startup.");
        }
    }

    private static string? GetFfmpegLocation()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        return Directory.Exists(toolsDirectory) ? toolsDirectory : null;
    }

    internal static string? FindDownloadedMediaFile(string tempDirectory)
        => Directory.Exists(tempDirectory)
            ? Directory.EnumerateFiles(tempDirectory, $"{MediaFileBase}.*", SearchOption.TopDirectoryOnly)
                .Where(path => !IsSidecarFileName(Path.GetFileName(path)))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault()
            : null;

    // Extensions yt-dlp writes alongside the media file (thumbnails, subtitles, partials). The
    // media file detector must skip these so a freshly-written thumbnail (e.g. media.webp) isn't
    // mistaken for the media file.
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".part", ".ytdl", ".temp",
        ".jpg", ".jpeg", ".png", ".webp", ".gif",
        ".vtt", ".srt", ".ass", ".ssa", ".lrc", ".ttml", ".sbv", ".dfxp",
        ".json3", ".srv1", ".srv2", ".srv3", ".scc"
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vtt", ".srt", ".ass", ".ssa", ".lrc", ".ttml", ".sbv", ".dfxp",
        ".json3", ".srv1", ".srv2", ".srv3", ".scc"
    };

    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private static bool IsSidecarFileName(string fileName)
        => fileName.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".live_chat.json", StringComparison.OrdinalIgnoreCase)
            || SidecarExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// Locates a yt-dlp <c>.info.json</c> sidecar in the same temp directory as the media
    /// file. Returns null when the caller didn't enable <c>--write-info-json</c>. The
    /// output template forces <c>media.%(ext)s</c>, so the sidecar is always
    /// <c>media.info.json</c>.
    /// </summary>
    private static async Task<InfoJsonSidecar?> ResolveInfoJsonSidecarAsync(string tempDirectory)
    {
        if (!Directory.Exists(tempDirectory))
            return null;

        var infoJsonPath = Path.Combine(tempDirectory, $"{MediaFileBase}.info.json");
        var file = new FileInfo(infoJsonPath);
        if (!file.Exists || file.Length == 0)
            return null;

        var hash = await ComputeXxHash128Async(file.FullName);
        return new InfoJsonSidecar(file.FullName, file.Name, file.Length, hash);
    }

    private sealed record InfoJsonSidecar(string TempFileRef, string FileName, long SizeBytes, string ContentHash);

    /// <summary>
    /// Collects the per-media thumbnail and caption sidecars yt-dlp wrote next to the media file
    /// (same temp directory, <c>media.&lt;ext&gt;</c> naming). The media file itself and the
    /// info.json sidecar are skipped by extension. Caption languages are parsed from the
    /// <c>media.&lt;lang&gt;.&lt;ext&gt;</c> filename.
    /// </summary>
    // internal for unit testing the sidecar classification logic.
    internal static async Task<(SidecarFileRef? Thumbnail, IReadOnlyList<SidecarFileRef> Captions)> ResolveAssetSidecarsAsync(
        string tempDirectory,
        string mediaFileRef)
    {
        if (!Directory.Exists(tempDirectory))
            return (null, []);

        var mediaFileName = Path.GetFileName(mediaFileRef);
        SidecarFileRef? thumbnail = null;
        var captions = new List<SidecarFileRef>();

        foreach (var path in Directory.EnumerateFiles(tempDirectory, $"{MediaFileBase}.*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, mediaFileName, StringComparison.Ordinal))
                continue;

            var ext = Path.GetExtension(fileName);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                continue;

            if (thumbnail is null && ThumbnailExtensions.Contains(ext))
            {
                thumbnail = new SidecarFileRef
                {
                    TempFileRef = path,
                    FileName = fileName,
                    SizeBytes = file.Length,
                    ContentHashXxh128 = await ComputeXxHash128Async(path)
                };
            }
            else if (SubtitleExtensions.Contains(ext))
            {
                captions.Add(new SidecarFileRef
                {
                    TempFileRef = path,
                    FileName = fileName,
                    SizeBytes = file.Length,
                    ContentHashXxh128 = await ComputeXxHash128Async(path),
                    LanguageCode = ParseCaptionLanguage(fileName)
                });
            }
        }

        return (thumbnail, captions);
    }

    /// <summary>Extracts the language tag from a <c>media.&lt;lang&gt;.&lt;ext&gt;</c> caption filename.</summary>
    internal static string ParseCaptionLanguage(string fileName)
    {
        var prefix = $"{MediaFileBase}.";
        var middle = fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fileName[prefix.Length..]
            : fileName;
        var lastDot = middle.LastIndexOf('.');
        var lang = lastDot > 0 ? middle[..lastDot] : null;
        return string.IsNullOrWhiteSpace(lang) ? "und" : lang;
    }

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
