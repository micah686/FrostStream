using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Globalization;
using FluentStorage.Blobs;
using Conduit.NATS;
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
/// Worker-side JetStream consumer for Download Flow V2 commands. DataBridge constructs storage
/// paths and owns deduplication; the Worker acquires and renews the exact durable dispatch lease,
/// then executes only the provider, filesystem, or storage I/O it was assigned.
///
/// Consumer durables and the FROSTSTREAM_DOWNLOAD_V2 stream are provisioned by
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
    IMessageBus messageBus,
    ITopologyManager topologyManager,
    IYtDlpClient ytDlp,
    IBlobStorageProvider blobStorageProvider,
    ISecretStore secretStore,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    PotOptionsApplier potOptionsApplier,
    IReturnYouTubeDislikeClient returnYouTubeDislikeClient,
    ILogger<DownloadCommandsConsumerService> logger) : BackgroundService
{
    private const string MediaFileBase = "media";
    private static readonly StreamName Stream = StreamName.From(DownloadTopology.StreamNameValue);
    private static readonly StreamName ArtifactStream = StreamName.From(ArtifactStorageTopology.StreamNameValue);
    private static readonly TimeSpan StorageProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingCancellationTtl = TimeSpan.FromMinutes(30);

    // yt-dlp downloads routinely run well past the JetStream AckWait for this consumer
    // (DownloadTopology: 2 minutes) — a slow sidecar fetch (e.g. subtitle rate-limiting) alone can
    // exceed it. Without renewing in-progress acks, JetStream redelivers the same command while the
    // original invocation is still running, which collides with the active-run cancellation gate below.
    private static readonly TimeSpan DownloadHeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly string _workerInstanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<(Guid JobId, Guid RunId), CancellationTokenSource> _activeRunCancellations = new();
    private readonly ConcurrentDictionary<(Guid JobId, Guid RunId), DownloadStage> _activeRunStages = new();
    private readonly ConcurrentDictionary<(Guid JobId, Guid RunId), DateTimeOffset> _pendingRunCancellations = new();
    private readonly ConcurrentDictionary<(Guid JobId, Guid RunId), DateTimeOffset> _userStopRequests = new();
    private ISubscription? _cancelSubscription;
    private CancellationToken _serviceStoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceStoppingToken = stoppingToken;
        // Remove any cookie scratch dirs left behind by a previous crash before serving traffic.
        SweepCookieScratchRoot();

        _cancelSubscription = await messageBus.SubscribeAsync<StopActiveDownloadRun>(
            DownloadSubjects.StopActiveRun,
            HandleStopActiveRunAsync,
            queueGroup: null,
            cancellationToken: stoppingToken);

        var options = workerOptions.Value;
        var tags = options.Tags;

        // Ensure per-tag consumers exist in JetStream before subscribing. EnsureConsumerAsync is
        // idempotent — multiple worker instances with the same tags race to create the same durable
        // consumers, and the second+ calls become no-ops.
        foreach (var tag in tags)
        {
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerFetchMetadataConsumer,        DownloadSubjects.FetchMetadataCommand,        tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(DownloadTopology.TaggedWorkerConsumerSpec(DownloadTopology.WorkerDownloadVideoConsumer,        DownloadSubjects.DownloadVideoCommand,        tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(ArtifactStorageTopology.TaggedWorkerConsumerSpec(ArtifactStorageTopology.WorkerUploadConsumer, ArtifactStorageSubjects.UploadObjectCommand, tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(ArtifactStorageTopology.TaggedWorkerConsumerSpec(ArtifactStorageTopology.WorkerDeleteTempConsumer, ArtifactStorageSubjects.DeleteTempFileCommand, tag), stoppingToken);
            await topologyManager.EnsureConsumerAsync(ArtifactStorageTopology.TaggedWorkerConsumerSpec(ArtifactStorageTopology.WorkerDeleteObjectConsumer, ArtifactStorageSubjects.DeleteUploadedObjectCommand, tag), stoppingToken);
            logger.LogInformation("Ensured tagged download consumers for tag '{Tag}'.", tag);
        }

        var consumerTasks = new List<Task>();

        // Subscribe to untagged consumers when this worker accepts jobs with no required tag.
        if (options.AcceptsUntaggedJobs || tags.Count == 0)
        {
            consumerTasks.Add(Consume<FetchMetadataCommand>(DownloadTopology.WorkerFetchMetadataConsumer,        HandleFetchMetadataAsync,        stoppingToken));
            consumerTasks.Add(Consume<DownloadVideoCommand>(DownloadTopology.WorkerDownloadVideoConsumer,        HandleDownloadVideoAsync,        stoppingToken));
            consumerTasks.Add(ConsumeArtifact<UploadObjectCommand>(ArtifactStorageTopology.WorkerUploadConsumer, HandleUploadObjectAsync, stoppingToken));
            consumerTasks.Add(ConsumeArtifact<DeleteTempFileCommand>(ArtifactStorageTopology.WorkerDeleteTempConsumer, HandleDeleteTempFileAsync, stoppingToken));
            consumerTasks.Add(ConsumeArtifact<DeleteUploadedObjectCommand>(ArtifactStorageTopology.WorkerDeleteObjectConsumer, HandleDeleteUploadedObjectAsync, stoppingToken));
        }

        // Subscribe to per-tag consumers.
        foreach (var tag in tags)
        {
            consumerTasks.Add(Consume<FetchMetadataCommand>($"{DownloadTopology.WorkerFetchMetadataConsumer}-{tag}",        HandleFetchMetadataAsync,        stoppingToken));
            consumerTasks.Add(Consume<DownloadVideoCommand>($"{DownloadTopology.WorkerDownloadVideoConsumer}-{tag}",        HandleDownloadVideoAsync,        stoppingToken));
            consumerTasks.Add(ConsumeArtifact<UploadObjectCommand>($"{ArtifactStorageTopology.WorkerUploadConsumer}-{tag}", HandleUploadObjectAsync, stoppingToken));
            consumerTasks.Add(ConsumeArtifact<DeleteTempFileCommand>($"{ArtifactStorageTopology.WorkerDeleteTempConsumer}-{tag}", HandleDeleteTempFileAsync, stoppingToken));
            consumerTasks.Add(ConsumeArtifact<DeleteUploadedObjectCommand>($"{ArtifactStorageTopology.WorkerDeleteObjectConsumer}-{tag}", HandleDeleteUploadedObjectAsync, stoppingToken));
        }

        logger.LogInformation(
            "Subscribed to {Count} download command consumers on stream {Stream}. Tags: [{Tags}] AcceptsUntaggedJobs: {AcceptsUntaggedJobs}",
            consumerTasks.Count,
            Stream.Value,
            string.Join(", ", tags),
            options.AcceptsUntaggedJobs);

        await Task.WhenAll(consumerTasks);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancelSubscription is not null)
        {
            await _cancelSubscription.StopAsync(cancellationToken);
            await _cancelSubscription.DisposeAsync();
            _cancelSubscription = null;
        }

        foreach (var cancellation in _activeRunCancellations.Values)
            await cancellation.CancelAsync();

        await base.StopAsync(cancellationToken);
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

    private Task ConsumeArtifact<TCommand>(
        string consumerName,
        Func<IJsMessageContext<TCommand>, Task> handler,
        CancellationToken stoppingToken)
        where TCommand : class, IFlowMessage
        => consumer.ConsumePullAsync<TCommand>(
            stream: ArtifactStream,
            consumer: ConsumerName.From(consumerName),
            handler: handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleFetchMetadataAsync(IJsMessageContext<FetchMetadataCommand> context)
    {
        var cmd = context.Message;
        var cookieScratch = GetCookieScratchDirectory(cmd.JobId);
        await using var executionLease = await TryAcquireExecutionLeaseAsync(context, cmd.Execution);
        if (cmd.Execution is not null && executionLease is null)
            return;
        using var operationCts = RegisterActiveRun(cmd.Execution, executionLease!.CancellationToken);

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
            if (!await ProbeStorageAsync(cmd, operationCts.Token))
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
                cookieFilePath: cookies.FilePath,
                logger);

            var metadataResult = await ytDlp.TryGetVideoInfoAsync(
                cmd.SourceUrl,
                ct: operationCts.Token,
                overrideOptions: potOptionsApplier.Apply(metadataOptions),
                fetchComments: cmd.FetchComments);
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
            info = await ReturnYouTubeDislikeMetadataEnricher.EnrichAsync(
                info,
                returnYouTubeDislikeClient,
                operationCts.Token);

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
                Execution = cmd.Execution,
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
        catch (OperationCanceledException ex) when (operationCts.IsCancellationRequested)
        {
            var failureKind = CancellationFailureKind(cmd.Execution);
            var message = failureKind == FailureKind.Stopped
                ? "Metadata fetch stopped by request."
                : "Metadata fetch interrupted because the Worker stopped or lost its lease.";
            await PublishMetadataFailedAsync(cmd, new OperationCanceledException(message, ex, operationCts.Token), failureKind);
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
            UnregisterActiveRun(cmd.Execution, operationCts);
            DeleteCookieScratch(cookieScratch);
        }
    }

    private async Task HandleDownloadVideoAsync(IJsMessageContext<DownloadVideoCommand> context)
    {
        var cmd = context.Message;
        await using var executionLease = await TryAcquireExecutionLeaseAsync(context, cmd.Execution);
        if (cmd.Execution is not null && executionLease is null)
            return;
        var tempDirectory = GetDownloadTempDirectory(cmd);
        var cookieScratch = GetCookieScratchDirectory(cmd.JobId);
        string? tempFileRef = null;
        DownloadProgressReporter? progress = null;
        var acquisitionWarnings = new List<DownloadStageWarning>();
        using var operationCts = RegisterActiveRun(cmd.Execution, executionLease!.CancellationToken);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var heartbeatTask = JetStreamHeartbeat.RunAsync(context, DownloadHeartbeatInterval, logger, "DownloadVideo", heartbeatCts.Token);

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
            try
            {
                await DispatchYtDlpAsync(cmd, tempDirectory, cookies.FilePath, progress, operationCts.Token);
            }
            catch (YtDlpException ex) when (YtDlpFailureDetails.IsSidecarOnlyFailure(ex))
            {
                // yt-dlp can exit non-zero after the primary media is complete because an optional
                // subtitle or thumbnail failed. Accept the existing media and report a warning;
                // do not hide a second yt-dlp invocation inside this application attempt.
                tempFileRef = FindDownloadedMediaFile(tempDirectory);
                if (tempFileRef is null)
                    throw;
                logger.LogWarning(ex,
                    "Sidecar-only failure for JobId {JobId}; accepting primary media with a warning.",
                    cmd.JobId);
                await progress.ReportPhaseAsync(
                    "Optional sidecar warning",
                    "Primary media completed, but an optional subtitle or thumbnail failed.");
                acquisitionWarnings.Add(new DownloadStageWarning
                {
                    Code = "optional_sidecar_acquire_failed",
                    Message = YtDlpFailureDetails.DescribeException(ex, sourceUrl: cmd.SourceUrl)
                });
            }
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
                Execution = cmd.Execution,
                TempFileRef = tempFileRef,
                FileName = fileInfo.Name,
                FileSizeBytes = fileInfo.Length,
                ContentHashXxh128 = contentHash,
                InfoJsonTempFileRef = infoJson?.TempFileRef,
                InfoJsonFileName = infoJson?.FileName,
                InfoJsonSizeBytes = infoJson?.SizeBytes,
                InfoJsonContentHashXxh128 = infoJson?.ContentHash,
                Thumbnail = thumbnail,
                Captions = captions,
                Warnings = acquisitionWarnings
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
        catch (OperationCanceledException ex) when (operationCts.IsCancellationRequested)
        {
            if (progress is not null)
                await progress.FlushAsync();

            var failureKind = CancellationFailureKind(cmd.Execution);
            logger.LogInformation(ex,
                "DownloadVideo {Outcome} for JobId {JobId} URL {SourceUrl}",
                failureKind == FailureKind.Stopped ? "stopped" : "interrupted", cmd.JobId, cmd.SourceUrl);
            await PublishDownloadFailedAsync(
                cmd,
                new OperationCanceledException(
                    failureKind == FailureKind.Stopped
                        ? "Download stopped by request."
                        : "Download interrupted because the Worker stopped or lost its lease.",
                    ex,
                    operationCts.Token),
                failureKind,
                tempFileRef ?? FindDownloadedMediaFile(tempDirectory) ?? tempDirectory);
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
            UnregisterActiveRun(cmd.Execution, operationCts);
            DeleteCookieScratch(cookieScratch);

            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { /* best-effort cleanup */ }
        }
    }

    private Task HandleStopActiveRunAsync(IMessageContext<StopActiveDownloadRun> context)
    {
        var cmd = context.Message;
        var runKey = (cmd.JobId, cmd.RunId);
        _userStopRequests[runKey] = DateTimeOffset.UtcNow;
        if (_activeRunCancellations.TryGetValue(runKey, out var cancellation)
            && (!_activeRunStages.TryGetValue(runKey, out var stage)
                || stage is not (DownloadStage.Cleanup or DownloadStage.Compensation)))
        {
            logger.LogInformation(
                "Stopping active download for JobId {JobId}. RequestedBy {RequestedBy} Reason {Reason}",
                cmd.JobId,
                "v2-control",
                cmd.Reason);
            cancellation.Cancel();
        }
        else if (_activeRunStages.TryGetValue(runKey, out var settlingStage)
                 && settlingStage is DownloadStage.Cleanup or DownloadStage.Compensation)
        {
            logger.LogInformation(
                "Stop recorded for JobId {JobId}, but active {Stage} is allowed to settle.",
                cmd.JobId, settlingStage);
        }
        else
        {
            CleanupPendingRunCancellations();
            _pendingRunCancellations[runKey] = DateTimeOffset.UtcNow;
            logger.LogDebug("Recorded pending cancel request for JobId {JobId}; no active download on this worker.", cmd.JobId);
        }

        return Task.CompletedTask;
    }

    private void CleanupPendingRunCancellations()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(PendingCancellationTtl);
        foreach (var (runKey, recordedAt) in _pendingRunCancellations)
        {
            if (recordedAt < cutoff)
            {
                _pendingRunCancellations.TryRemove(runKey, out _);
                _userStopRequests.TryRemove(runKey, out _);
            }
        }
    }

    private CancellationTokenSource RegisterActiveRun(
        DownloadExecutionIdentity? execution,
        CancellationToken leaseToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_serviceStoppingToken, leaseToken);
        if (execution is null)
            return cancellation;

        var runKey = (execution.JobId, execution.RunId);
        _activeRunStages[runKey] = execution.Stage;
        if (!_activeRunCancellations.TryAdd(runKey, cancellation))
        {
            _activeRunStages.TryRemove(runKey, out _);
            cancellation.Dispose();
            throw new InvalidOperationException(
                $"Download job {execution.JobId} run {execution.RunId} already has an active stage on this worker.");
        }

        // Cleanup and compensation must settle even after the user has asked to stop. Cancelling
        // those operations would turn an otherwise clean stop into a residual-object failure.
        if (execution.Stage is DownloadStage.Cleanup or DownloadStage.Compensation)
        {
            _pendingRunCancellations.TryRemove(runKey, out _);
            _userStopRequests.TryRemove(runKey, out _);
        }
        else if (_pendingRunCancellations.TryRemove(runKey, out _))
        {
            logger.LogInformation(
                "Applying pending stop to active {Stage} stage for JobId {JobId} RunId {RunId}.",
                execution.Stage, execution.JobId, execution.RunId);
            cancellation.Cancel();
        }

        return cancellation;
    }

    private void UnregisterActiveRun(
        DownloadExecutionIdentity? execution,
        CancellationTokenSource cancellation)
    {
        if (execution is null)
            return;
        var runKey = (execution.JobId, execution.RunId);
        ((ICollection<KeyValuePair<(Guid JobId, Guid RunId), CancellationTokenSource>>)_activeRunCancellations)
            .Remove(new KeyValuePair<(Guid JobId, Guid RunId), CancellationTokenSource>(runKey, cancellation));
        _activeRunStages.TryRemove(runKey, out _);
        _pendingRunCancellations.TryRemove(runKey, out _);
        _userStopRequests.TryRemove(runKey, out _);
    }

    private FailureKind CancellationFailureKind(DownloadExecutionIdentity? execution)
    {
        if (execution is null)
            return FailureKind.Cancelled;
        return _userStopRequests.ContainsKey((execution.JobId, execution.RunId))
            ? FailureKind.Stopped
            : FailureKind.Interrupted;
    }

    private async Task HandleUploadObjectAsync(IJsMessageContext<UploadObjectCommand> context)
    {
        var cmd = context.Message;
        await using var executionLease = await TryAcquireExecutionLeaseAsync(context, cmd.Execution);
        if (cmd.Execution is not null && executionLease is null)
            return;
        using var operationCts = RegisterActiveRun(cmd.Execution, executionLease!.CancellationToken);

        try
        {
            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey, operationCts.Token);
            long contentLength;

            if (cmd.InlineContent is { } inlineBytes)
            {
                logger.LogInformation(
                    "Upload (inline) started for JobId {JobId} Attempt {Attempt} SizeBytes {SizeBytes} StorageKey {StorageKey} StoragePath {StoragePath}",
                    cmd.JobId,
                    cmd.Attempt,
                    inlineBytes.Length,
                    cmd.StorageKey,
                    cmd.StoragePath);

                await using var memStream = new MemoryStream(inlineBytes, writable: false);
                await storage.WriteAsync(cmd.StoragePath, memStream, append: false, operationCts.Token);
                contentLength = inlineBytes.Length;

                logger.LogInformation(
                    "Upload (inline) completed for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {SizeBytes}",
                    cmd.JobId,
                    cmd.Attempt,
                    cmd.StorageKey,
                    cmd.StoragePath,
                    contentLength);
            }
            else if (cmd.TempFileRef is { } tempRef)
            {
                var fileInfo = new FileInfo(tempRef);
                if (!fileInfo.Exists)
                    throw new FileNotFoundException("Temp file to upload was not found.", tempRef);

                logger.LogInformation(
                    "Upload started for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef} SizeBytes {FileSizeBytes} StorageKey {StorageKey} StoragePath {StoragePath}",
                    cmd.JobId,
                    cmd.Attempt,
                    tempRef,
                    fileInfo.Length,
                    cmd.StorageKey,
                    cmd.StoragePath);

                await using (var stream = File.OpenRead(fileInfo.FullName))
                {
                    if (cmd.VerifyHashWhileStreaming)
                    {
                        await using var hashingStream = new XxHash128ReadStream(stream);
                        await storage.WriteAsync(cmd.StoragePath, hashingStream, append: false, operationCts.Token);
                        var observedHash = hashingStream.GetHash();
                        if (!string.Equals(observedHash, cmd.ContentHashXxh128, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Upload hash verification failed. Expected {cmd.ContentHashXxh128}, observed {observedHash}.");
                        }
                    }
                    else
                    {
                        await storage.WriteAsync(cmd.StoragePath, stream, append: false, operationCts.Token);
                    }
                }
                contentLength = fileInfo.Length;

                logger.LogInformation(
                    "Upload completed for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {FileSizeBytes} ContentHash {ContentHashXxh128}",
                    cmd.JobId,
                    cmd.Attempt,
                    cmd.StorageKey,
                    cmd.StoragePath,
                    contentLength,
                    cmd.ContentHashXxh128);
            }
            else
            {
                throw new InvalidOperationException("UploadObjectCommand has neither TempFileRef nor InlineContent.");
            }

            await Publish(ArtifactStorageSubjects.UploadCompleted, new UploadCompleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                TempFileRef = cmd.TempFileRef,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = null,
                ContentHashXxh128 = cmd.ContentHashXxh128,
                ContentLengthBytes = contentLength,
                Kind = cmd.Kind
            });
            await context.AckAsync();
        }
        catch (OperationCanceledException ex) when (operationCts.IsCancellationRequested)
        {
            var failureKind = CancellationFailureKind(cmd.Execution);
            await PublishUploadFailedAsync(cmd, new OperationCanceledException(
                failureKind == FailureKind.Stopped
                    ? "Upload stopped by request."
                    : "Upload interrupted because the Worker stopped or lost its lease.",
                ex,
                operationCts.Token), failureKind);
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
        finally
        {
            UnregisterActiveRun(cmd.Execution, operationCts);
        }
    }

    private async Task HandleDeleteTempFileAsync(IJsMessageContext<DeleteTempFileCommand> context)
    {
        var cmd = context.Message;
        await using var executionLease = await TryAcquireExecutionLeaseAsync(context, cmd.Execution);
        if (cmd.Execution is not null && executionLease is null)
            return;
        using var operationCts = RegisterActiveRun(cmd.Execution, executionLease!.CancellationToken);

        try
        {
            operationCts.Token.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Temp file cleanup started for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef}",
                cmd.JobId,
                cmd.Attempt,
                cmd.TempFileRef);

            DeleteTempFileRef(cmd.TempFileRef);
            operationCts.Token.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Temp file cleanup completed for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef}",
                cmd.JobId,
                cmd.Attempt,
                cmd.TempFileRef);

            await Publish(ArtifactStorageSubjects.TempFileDeleted, new TempFileDeleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                TempFileRef = cmd.TempFileRef
            });
            await context.AckAsync();
        }
        catch (OperationCanceledException ex) when (operationCts.IsCancellationRequested)
        {
            var failureKind = CancellationFailureKind(cmd.Execution);
            await Publish(ArtifactStorageSubjects.TempFileDeleteFailed, new TempFileDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                TempFileRef = cmd.TempFileRef,
                FailureKind = failureKind,
                ErrorMessage = failureKind == FailureKind.Stopped
                    ? "Temp cleanup stopped by request."
                    : $"Temp cleanup interrupted because the Worker stopped or lost its lease: {ex.Message}"
            });
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DeleteTempFile failed for JobId {JobId} TempFileRef {TempFileRef}",
                cmd.JobId, cmd.TempFileRef);
            await Publish(ArtifactStorageSubjects.TempFileDeleteFailed, new TempFileDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                TempFileRef = cmd.TempFileRef,
                FailureKind = DeleteFailureKind(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
        finally
        {
            UnregisterActiveRun(cmd.Execution, operationCts);
        }
    }

    private async Task HandleDeleteUploadedObjectAsync(IJsMessageContext<DeleteUploadedObjectCommand> context)
    {
        var cmd = context.Message;
        await using var executionLease = await TryAcquireExecutionLeaseAsync(context, cmd.Execution);
        if (cmd.Execution is not null && executionLease is null)
            return;
        using var operationCts = RegisterActiveRun(cmd.Execution, executionLease!.CancellationToken);

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

            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey, operationCts.Token);
            await storage.DeleteAsync([cmd.StoragePath], operationCts.Token);

            logger.LogInformation(
                "Uploaded object cleanup completed for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId,
                cmd.Attempt,
                cmd.StorageKey,
                cmd.StoragePath);

            await Publish(ArtifactStorageSubjects.UploadedObjectDeleted, new UploadedObjectDeleted
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = cmd.StorageVersion
            });
            await context.AckAsync();
        }
        catch (OperationCanceledException ex) when (operationCts.IsCancellationRequested)
        {
            var failureKind = CancellationFailureKind(cmd.Execution);
            await Publish(ArtifactStorageSubjects.UploadedObjectDeleteFailed, new UploadedObjectDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = cmd.StorageVersion,
                FailureKind = failureKind,
                ErrorMessage = failureKind == FailureKind.Stopped
                    ? "Object compensation stopped by request."
                    : $"Object compensation interrupted because the Worker stopped or lost its lease: {ex.Message}"
            });
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DeleteUploadedObject failed for JobId {JobId} StorageKey {StorageKey} StoragePath {StoragePath}",
                cmd.JobId, cmd.StorageKey, cmd.StoragePath);
            await Publish(ArtifactStorageSubjects.UploadedObjectDeleteFailed, new UploadedObjectDeleteFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                Execution = cmd.Execution,
                StorageKey = cmd.StorageKey,
                StoragePath = cmd.StoragePath,
                StorageVersion = cmd.StorageVersion,
                FailureKind = DeleteFailureKind(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
        finally
        {
            UnregisterActiveRun(cmd.Execution, operationCts);
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
    private async Task<bool> ProbeStorageAsync(FetchMetadataCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(StorageProbeTimeout);
            var storage = await blobStorageProvider.GetAsync(cmd.StorageKey, cts.Token);
            await storage.ListAsync(new ListOptions { MaxResults = 1 }, cts.Token);

            logger.LogDebug(
                "Storage probe succeeded for JobId {JobId} StorageKey {StorageKey}.",
                cmd.JobId,
                cmd.StorageKey);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                Execution = cmd.Execution,
                FailureKind = FailureKind.Permanent,
                ErrorCode = "storage_unavailable",
                ErrorMessage = $"Storage backend '{cmd.StorageKey}' is unreachable from this worker: {ex.Message}"
            });
            return false;
        }
    }

    private async Task Publish<T>(string subject, T message) where T : IFlowMessage
    {
        await publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));
        if (message is DownloadProgress || ExecutionOf(message) is not { } execution)
            return;

        var failure = FailureKindOf(message);
        if (failure is FailureKind.Stopped or FailureKind.Cancelled)
        {
            var stopped = new DownloadStageStopped
            {
                Execution = execution,
                MessageId = DeterministicGuid.Create(message.MessageId, "/stage-stopped"),
                CausationId = message.MessageId,
                OperationKey = $"{message.OperationKey}/stage-stopped",
                OccurredAt = clock.GetCurrentInstant(),
                WorkerInstanceId = _workerInstanceId,
                Reason = FailureMessageOf(message)
            };
            await publisher.PublishAsync(DownloadSubjects.StageStopped, stopped, stopped.MessageId.ToString("N"));
        }
        else if (failure is { } failedKind)
        {
            var failed = new DownloadStageFailed
            {
                Execution = execution,
                MessageId = DeterministicGuid.Create(message.MessageId, "/stage-failed"),
                CausationId = message.MessageId,
                OperationKey = $"{message.OperationKey}/stage-failed",
                OccurredAt = clock.GetCurrentInstant(),
                WorkerInstanceId = _workerInstanceId,
                FailureKind = failedKind,
                ErrorCode = FailureCodeOf(message),
                ErrorMessage = FailureMessageOf(message) ?? "Worker stage failed."
            };
            await publisher.PublishAsync(DownloadSubjects.StageFailed, failed, failed.MessageId.ToString("N"));
        }
        else
        {
            var succeeded = new DownloadStageSucceeded
            {
                Execution = execution,
                MessageId = DeterministicGuid.Create(message.MessageId, "/stage-succeeded"),
                CausationId = message.MessageId,
                OperationKey = $"{message.OperationKey}/stage-succeeded",
                OccurredAt = clock.GetCurrentInstant(),
                WorkerInstanceId = _workerInstanceId
            };
            await publisher.PublishAsync(DownloadSubjects.StageSucceeded, succeeded, succeeded.MessageId.ToString("N"));
        }
    }

    private async Task<WorkerExecutionLease?> TryAcquireExecutionLeaseAsync<T>(
        IJsMessageContext<T> context,
        DownloadExecutionIdentity? execution)
        where T : class
    {
        if (execution is null)
            return WorkerExecutionLease.Noop;

        AcquireDownloadLeaseResponse? response;
        try
        {
            response = await messageBus.RequestAsync<AcquireDownloadLeaseRequest, AcquireDownloadLeaseResponse>(
                DownloadSubjects.AcquireLeaseRequest,
                new AcquireDownloadLeaseRequest
                {
                    Execution = execution,
                    WorkerInstanceId = _workerInstanceId,
                    OccurredAt = clock.GetCurrentInstant()
                },
                LeaseRequestTimeout,
                _serviceStoppingToken);
        }
        catch (Exception ex) when (!_serviceStoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not acquire V2 lease for DispatchId {DispatchId}; nacking transport delivery.", execution.DispatchId);
            await context.NackAsync();
            return null;
        }

        if (response is null)
        {
            await context.NackAsync();
            return null;
        }
        if (!response.Granted)
        {
            logger.LogInformation("Rejected stale V2 dispatch {DispatchId}: {Reason}", execution.DispatchId, response.RejectionCode);
            await context.AckAsync();
            return null;
        }

        if (response.StopRequested)
        {
            // The command was durably published before Stop won the database gate. Claim it only
            // to produce the matching Stopped result that releases the immutable flow waiter; no
            // provider or storage operation is allowed to begin.
            _userStopRequests[(execution.JobId, execution.RunId)] = DateTimeOffset.UtcNow;
        }

        var started = new DownloadStageStarted
        {
            Execution = execution,
            MessageId = DeterministicGuid.Create(execution.DispatchId, "/stage-started"),
            CausationId = execution.DispatchId,
            OperationKey = $"dispatch/{execution.DispatchId:N}/stage-started",
            OccurredAt = clock.GetCurrentInstant(),
            WorkerInstanceId = _workerInstanceId
        };
        await publisher.PublishAsync(DownloadSubjects.StageStarted, started, started.MessageId.ToString("N"));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceStoppingToken);
        var heartbeat = RunLeaseHeartbeatAsync(execution, cts);
        if (response.StopRequested)
            await cts.CancelAsync();
        return new WorkerExecutionLease(cts, heartbeat);
    }

    private async Task RunLeaseHeartbeatAsync(DownloadExecutionIdentity execution, CancellationTokenSource leaseCts)
    {
        var missed = 0;
        try
        {
            using var timer = new PeriodicTimer(LeaseHeartbeatInterval);
            while (await timer.WaitForNextTickAsync(leaseCts.Token))
            {
                RenewDownloadLeaseResponse? response = null;
                try
                {
                    response = await messageBus.RequestAsync<RenewDownloadLeaseRequest, RenewDownloadLeaseResponse>(
                        DownloadSubjects.RenewLeaseRequest,
                        new RenewDownloadLeaseRequest
                        {
                            DispatchId = execution.DispatchId,
                            RunId = execution.RunId,
                            WorkerInstanceId = _workerInstanceId,
                            OccurredAt = clock.GetCurrentInstant()
                        },
                        LeaseRequestTimeout,
                        leaseCts.Token);
                }
                catch (Exception ex) when (!leaseCts.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "V2 lease renewal failed for DispatchId {DispatchId}.", execution.DispatchId);
                }

                if (response?.Renewed == true)
                {
                    missed = 0;
                    var heartbeat = new DownloadStageHeartbeat
                    {
                        Execution = execution,
                        MessageId = Guid.NewGuid(),
                        CausationId = execution.DispatchId,
                        OperationKey = $"dispatch/{execution.DispatchId:N}/heartbeat/{clock.GetCurrentInstant().ToUnixTimeTicks()}",
                        OccurredAt = clock.GetCurrentInstant(),
                        WorkerInstanceId = _workerInstanceId
                    };
                    await publisher.PublishAsync(DownloadSubjects.StageHeartbeat, heartbeat, heartbeat.MessageId.ToString("N"));
                    continue;
                }

                missed++;
                if (missed < 3)
                    continue;
                logger.LogError("V2 lease lost for DispatchId {DispatchId}; cancelling local work.", execution.DispatchId);
                await leaseCts.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (leaseCts.IsCancellationRequested)
        {
        }
    }

    private static DownloadExecutionIdentity? ExecutionOf(IFlowMessage message) => message switch
    {
        MetadataFetched x => x.Execution,
        MetadataFetchFailed x => x.Execution,
        DownloadCompleted x => x.Execution,
        DownloadFailed x => x.Execution,
        UploadCompleted x => x.Execution,
        UploadFailed x => x.Execution,
        TempFileDeleted x => x.Execution,
        TempFileDeleteFailed x => x.Execution,
        UploadedObjectDeleted x => x.Execution,
        UploadedObjectDeleteFailed x => x.Execution,
        _ => null
    };

    private static FailureKind? FailureKindOf(IFlowMessage message) => message switch
    {
        MetadataFetchFailed x => x.FailureKind,
        DownloadFailed x => x.FailureKind,
        UploadFailed x => x.FailureKind,
        TempFileDeleteFailed x => x.FailureKind,
        UploadedObjectDeleteFailed x => x.FailureKind,
        _ => null
    };

    private static string? FailureCodeOf(IFlowMessage message) => message switch
    {
        MetadataFetchFailed x => x.ErrorCode,
        DownloadFailed x => x.ErrorCode,
        UploadFailed x => x.ErrorCode,
        _ => null
    };

    private static string? FailureMessageOf(IFlowMessage message) => message switch
    {
        MetadataFetchFailed x => x.ErrorMessage,
        DownloadFailed x => x.ErrorMessage,
        UploadFailed x => x.ErrorMessage,
        TempFileDeleteFailed x => x.ErrorMessage,
        UploadedObjectDeleteFailed x => x.ErrorMessage,
        _ => null
    };

    private sealed class WorkerExecutionLease(
        CancellationTokenSource? cancellation,
        Task? heartbeat) : IAsyncDisposable
    {
        public static readonly WorkerExecutionLease Noop = new(null, null);
        public CancellationToken CancellationToken => cancellation?.Token ?? CancellationToken.None;

        public async ValueTask DisposeAsync()
        {
            if (cancellation is null)
                return;
            await cancellation.CancelAsync();
            if (heartbeat is not null)
            {
                try { await heartbeat; } catch (OperationCanceledException) { }
            }
            cancellation.Dispose();
        }
    }

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
    {
        var providerFailure = YtDlpFailureDetails.ClassifyProviderAccessFailure(ex, sourceUrl: cmd.SourceUrl);
        return PublishFailureAsync(DownloadSubjects.MetadataFetchFailed, cmd, envelope => new MetadataFetchFailed
        {
            JobId = envelope.JobId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            MessageId = envelope.MessageId,
            OperationKey = envelope.OperationKey,
            OccurredAt = envelope.OccurredAt,
            Attempt = envelope.Attempt,
            Execution = envelope.Execution,
            FailureKind = failureKind,
            ErrorCode = providerFailure?.ErrorCode ?? YtDlpFailureDetails.ErrorCode(ex, sourceUrl: cmd.SourceUrl),
            Provider = providerFailure?.Provider ?? YtDlpFailureDetails.ResolveProvider(sourceUrl: cmd.SourceUrl),
            HaltProviderDownloads = providerFailure?.HaltProviderDownloads ?? false,
            ErrorMessage = providerFailure?.Description ?? YtDlpFailureDetails.DescribeException(ex, sourceUrl: cmd.SourceUrl)
        });
    }

    private Task PublishDownloadFailedAsync(
        DownloadVideoCommand cmd,
        Exception ex,
        FailureKind failureKind,
        string? tempFileRef)
    {
        var providerFailure = YtDlpFailureDetails.ClassifyProviderAccessFailure(ex, sourceUrl: cmd.SourceUrl);
        return PublishFailureAsync(DownloadSubjects.DownloadFailed, cmd, envelope => new DownloadFailed
        {
            JobId = envelope.JobId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            MessageId = envelope.MessageId,
            OperationKey = envelope.OperationKey,
            OccurredAt = envelope.OccurredAt,
            Attempt = envelope.Attempt,
            Execution = envelope.Execution,
            FailureKind = failureKind,
            ErrorCode = providerFailure?.ErrorCode ?? YtDlpFailureDetails.ErrorCode(ex, sourceUrl: cmd.SourceUrl),
            Provider = providerFailure?.Provider ?? YtDlpFailureDetails.ResolveProvider(sourceUrl: cmd.SourceUrl),
            HaltProviderDownloads = providerFailure?.HaltProviderDownloads ?? false,
            ErrorMessage = providerFailure?.Description ?? YtDlpFailureDetails.DescribeException(ex, sourceUrl: cmd.SourceUrl),
            TempFileRef = tempFileRef
        });
    }

    private Task PublishUploadFailedAsync(UploadObjectCommand cmd, Exception ex, FailureKind failureKind)
        => PublishFailureAsync(ArtifactStorageSubjects.UploadFailed, cmd, envelope => new UploadFailed
        {
            JobId = envelope.JobId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            MessageId = envelope.MessageId,
            OperationKey = envelope.OperationKey,
            OccurredAt = envelope.OccurredAt,
            Attempt = envelope.Attempt,
            Execution = envelope.Execution,
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
        int Attempt,
        DownloadExecutionIdentity? Execution)
    {
        public static FailureEnvelope From(IFlowMessage command, IClock clock)
            => new(
                command.JobId,
                command.CorrelationId,
                command.MessageId,
                DeterministicGuid.Create(command.MessageId, "/failed"),
                $"{command.OperationKey}/failed",
                clock.GetCurrentInstant(),
                command.Attempt,
                command switch
                {
                    FetchMetadataCommand x => x.Execution,
                    DownloadVideoCommand x => x.Execution,
                    UploadObjectCommand x => x.Execution,
                    DeleteTempFileCommand x => x.Execution,
                    DeleteUploadedObjectCommand x => x.Execution,
                    _ => null
                });
    }


    #region Helpers
    private Task DispatchYtDlpAsync(
        DownloadVideoCommand cmd,
        string tempDirectory,
        string? cookieFilePath,
        DownloadProgressReporter progress,
        CancellationToken cancellationToken)
    {
        var ytDlpOptions = ApplyOperationalDefaults(YtDlpOptionsMerger.Merge(
            cmd.YtDlpOptions,
            ffmpegLocation: GetFfmpegLocation(),
            cookieFilePath: cookieFilePath,
            logger));

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
                progress,
                cancellationToken);
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
            progress,
            cancellationToken);
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
    ///
    /// When POT is enabled and the shim is up, the bgutil <c>--extractor-args</c> and
    /// <c>--plugin-dirs</c> are appended (never replacing caller values) so YouTube downloads can
    /// fetch a Proof-of-Origin token via the loopback shim → NATS broker → provider path.
    /// </summary>
    private YtDlpOptions ApplyOperationalDefaults(YtDlpOptions options)
    {
        options = options with
        {
            VideoSelection = options.VideoSelection with { NoPlaylist = true },
            Filesystem = options.Filesystem with { NoPart = true },
            VerbositySimulation = options.VerbositySimulation with { Newline = true },
            Thumbnail = options.Thumbnail with { WriteThumbnail = !options.Thumbnail.NoWriteThumbnail }
        };

        // Non-null input always yields non-null output (POT args are appended, never replacing).
        return potOptionsApplier.Apply(options)!;
    }

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

    private sealed class XxHash128ReadStream(Stream inner) : Stream
    {
        private readonly XxHash128 _hasher = new();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
                _hasher.Append(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
                _hasher.Append(buffer.Span[..read]);
            return read;
        }

        public string GetHash()
        {
            Span<byte> hash = stackalloc byte[16];
            _hasher.GetCurrentHash(hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    #endregion
}
