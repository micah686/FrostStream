using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Shared;
using Shared.Jobs;
using Shared.Messages;
using Shared.Storage;
using Worker.Services;

namespace Worker.Handlers;

/// <summary>
/// Handles file download requests from the JetStream file-processors consumer.
/// Implements the full Saga pattern: yt-dlp → idempotency check → download → upload → commit → (rollback on failure).
/// </summary>
public class FileProcessHandler
{
    private static readonly string InstrumentationName =
        typeof(FileProcessHandler).Assembly.GetName().Name ?? "Worker";

    private static readonly ActivitySource SagaActivitySource = new(InstrumentationName);
    private static readonly Meter SagaMeter = new(InstrumentationName);
    private static readonly Counter<long> SagaPhaseCounter =
        SagaMeter.CreateCounter<long>("froststream.saga.phase.total");
    private static readonly Histogram<double> SagaPhaseDuration =
        SagaMeter.CreateHistogram<double>("froststream.saga.phase.duration.ms", unit: "ms");
    private static readonly Counter<long> CompensationCounter =
        SagaMeter.CreateCounter<long>("froststream.saga.compensation.total");

    private readonly IMessageBus _messageBus;
    private readonly YtDlpService _ytDlp;
    private readonly ILogger<FileProcessHandler> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    private static readonly TimeSpan NatsTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InProgressHeartbeatInterval = TimeSpan.FromSeconds(10);

    public FileProcessHandler(
        IMessageBus messageBus,
        YtDlpService ytDlp,
        ILogger<FileProcessHandler> logger)
    {
        _messageBus = messageBus;
        _ytDlp = ytDlp;
        _logger = logger;

        // Polly retry pipeline for transient network errors on storage & NATS operations
        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<HttpRequestException>()
            })
            .AddTimeout(TimeSpan.FromMinutes(10))
            .Build();
    }

    public async Task HandleAsync(IJsMessageContext<FileDownloadRequest> context)
    {
        var request = context.Message;
        var jobId = request.JobId;

        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = jobId,
            ["StorageKey"] = request.StorageKey,
            ["DeliveryAttempt"] = context.NumDelivered,
            ["JetStreamSequence"] = context.Sequence,
            ["Redelivered"] = context.Redelivered
        });

        using var sagaActivity = SagaActivitySource.StartActivity("froststream.worker.process", ActivityKind.Consumer);
        sagaActivity?.SetTag("job.id", jobId);
        sagaActivity?.SetTag("storage.key", request.StorageKey);
        sagaActivity?.SetTag("video.url", request.Url);
        sagaActivity?.SetTag("messaging.redelivered", context.Redelivered);
        sagaActivity?.SetTag("messaging.delivery_attempt", context.NumDelivered);

        _logger.LogInformation("Processing download request for JobId: {JobId}, Url: {Url}, StorageKey: {StorageKey}",
            jobId, request.Url, request.StorageKey);

        string? uploadedBlobPath = null;
        IBlobStorage? storage = null;
        string? idempotencyKey = null;
        bool commitOutcomeUncertain = false;
        var dataDir = Path.Combine(Path.GetTempPath(), "froststream", "data", jobId.ToString());

        try
        {
            // ── Phase 1: Pre-flight metadata fetch ──────────────────────────
            var metadata = await ExecuteObservedPhaseAsync(
                "metadata",
                jobId,
                () => _ytDlp.FetchMetadataAsync(request.Url));

            idempotencyKey = YtDlpService.ComputeIdempotencyKey(
                request.Url, request.StorageKey, metadata.SourceLastModified);

            sagaActivity?.SetTag("idempotency.key", idempotencyKey);
            sagaActivity?.SetTag("video.platform", metadata.Platform);
            sagaActivity?.SetTag("video.source_last_modified", metadata.SourceLastModified?.ToString("O"));

            _logger.LogInformation("Computed IdempotencyKey: {Key} for video {VideoId} ({Platform})",
                idempotencyKey, metadata.Id, metadata.Platform);

            // ── Phase 2: Idempotency & state tracking check via DataBridge ──
            var startResponse = await ExecuteObservedPhaseAsync(
                "job_start",
                jobId,
                () => _messageBus.RequestAsync<JobStartRequest, JobStartResponse>(
                    Subjects.JobStart,
                    new JobStartRequest(jobId, idempotencyKey, request.StorageKey, request.Url),
                    NatsTimeout));

            if (startResponse == null)
            {
                throw new InvalidOperationException(
                    $"DataBridge returned null for job.start (JobId: {jobId})");
            }

            if (!startResponse.Proceed)
            {
                await HandleExistingJobStateAsync(context, request, metadata, idempotencyKey, startResponse.Reason, jobId);
                return;
            }

            // ── Phase 3: Download the video file locally ────────────────────
            var downloadResult = await ExecuteObservedPhaseAsync(
                "download",
                jobId,
                () => ExecuteWithInProgressHeartbeatsAsync(
                    context,
                    phase: "download",
                    () => _ytDlp.DownloadAsync(request.Url, dataDir)));

            _logger.LogInformation("Downloaded video {VideoId}: {FilePath} (hash: {Hash})",
                downloadResult.Metadata.Id, downloadResult.LocalFilePath, downloadResult.FileHash);

            // ── Phase 4: Get storage config from DataBridge ─────────────────
            var storageCfg = await ExecuteObservedPhaseAsync(
                "storage_config",
                jobId,
                () => _messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
                    Subjects.StorageConfig,
                    new StorageConfigRequest(request.StorageKey),
                    NatsTimeout));

            if (storageCfg == null || !storageCfg.Found)
            {
                throw new InvalidOperationException(
                    $"Storage config not found for key: {request.StorageKey}");
            }

            storage = FluentStorageProvider.CreateStorage(storageCfg);

            // ── Phase 5: Upload to storage ──────────────────────────────────
            var extension = Path.GetExtension(downloadResult.LocalFilePath);
            uploadedBlobPath = $"{metadata.Platform}/{metadata.Id}/v{downloadResult.FileHash}{extension}";

            _logger.LogInformation("Uploading to storage path: {Path}", uploadedBlobPath);

            await ExecuteObservedPhaseAsync(
                "upload",
                jobId,
                () => ExecuteWithInProgressHeartbeatsAsync(
                    context,
                    phase: "upload",
                    async () =>
                    {
                        await _resiliencePipeline.ExecuteAsync(async token =>
                        {
                            await using var fs = File.OpenRead(downloadResult.LocalFilePath);
                            await storage.WriteAsync(uploadedBlobPath, fs, cancellationToken: token);
                        });
                    }));

            _logger.LogInformation("Upload complete for {Path}", uploadedBlobPath);

            // Explicit saga transition: artifact is uploaded, awaiting DB commit.
            await ExecuteObservedPhaseAsync(
                "mark_uploaded_pending_commit",
                jobId,
                () => MarkUploadedPendingCommitAsync(jobId, uploadedBlobPath, downloadResult.FileHash));

            // ── Phase 6: Atomic commit via DataBridge ────────────────────────
            VideoCommitResponse? commitResponse;
            try
            {
                commitResponse = await ExecuteObservedPhaseAsync(
                    "commit",
                    jobId,
                    () => ExecuteWithInProgressHeartbeatsAsync(
                        context,
                        phase: "commit",
                        async () => await _resiliencePipeline.ExecuteAsync(async token =>
                        {
                            return await _messageBus.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
                                Subjects.VideoCommit,
                                new VideoCommitRequest(
                                    jobId,
                                    idempotencyKey,
                                    request.StorageKey,
                                    uploadedBlobPath,
                                    downloadResult.FileHash,
                                    downloadResult.Metadata.RawJson,
                                    downloadResult.Metadata.Platform,
                                    downloadResult.Metadata.SourceLastModified),
                                NatsTimeout,
                                token);
                        })));
            }
            catch
            {
                commitOutcomeUncertain = true;
                throw;
            }

            if (commitResponse == null || !commitResponse.Success)
            {
                var errorMsg = commitResponse?.ErrorMessage ?? "null response";
                throw new InvalidOperationException($"DataBridge rejected commit: {errorMsg}");
            }

            _logger.LogInformation("VideoCommit succeeded for JobId: {JobId}", jobId);

            // ── Cleanup local temp files ────────────────────────────────────
            CleanupLocalData(dataDir);
        }
        catch (Exception ex)
        {
            if (ex is RetryLaterException)
            {
                _logger.LogWarning(ex,
                    "Deferring failure handling for JobId {JobId}. The message will be retried without compensation.",
                    jobId);
                CleanupLocalData(dataDir);
                throw;
            }

            _logger.LogError(ex, "Failed to process download for JobId: {JobId}", jobId);

            // If commit outcome is uncertain, prefer retry + reconciliation over destructive compensation.
            if (uploadedBlobPath != null && commitOutcomeUncertain)
            {
                var currentStatus = await TryGetJobStatusAsync(jobId);
                var parsedStatus = currentStatus == null
                    ? JobStatus.Unknown
                    : JobStatusCodec.Parse(currentStatus.Status);

                if (parsedStatus == JobStatus.Completed)
                {
                    _logger.LogWarning(
                        "Detected completed commit for JobId {JobId} after exception path. Skipping rollback and JobFail.",
                        jobId);
                    CleanupLocalData(dataDir);
                    return;
                }

                if (currentStatus == null || parsedStatus == JobStatus.UploadedPendingCommit)
                {
                    throw new RetryLaterException(
                        $"Commit outcome is uncertain for JobId {jobId}. Deferring compensation until retry can reconcile.",
                        ex);
                }
            }

            // ── Compensating transaction: rollback uploaded file ─────────────
            if (storage != null && uploadedBlobPath != null)
            {
                try
                {
                    _logger.LogWarning("Rolling back: deleting uploaded blob {Path}", uploadedBlobPath);
                    await storage.DeleteAsync(new[] { uploadedBlobPath });
                    CompensationCounter.Add(1,
                        new KeyValuePair<string, object?>("action", "delete_blob"),
                        new KeyValuePair<string, object?>("outcome", "success"));
                }
                catch (Exception rollbackEx)
                {
                    CompensationCounter.Add(1,
                        new KeyValuePair<string, object?>("action", "delete_blob"),
                        new KeyValuePair<string, object?>("outcome", "error"));
                    _logger.LogError(rollbackEx, "Failed to rollback uploaded blob {Path}", uploadedBlobPath);
                }
            }

            // Notify DataBridge of job failure
            try
            {
                await _messageBus.RequestAsync<JobFailRequest, JobFailResponse>(
                    Subjects.JobFail,
                    new JobFailRequest(jobId, ex.Message, ex.StackTrace),
                    NatsTimeout);
            }
            catch (Exception failEx)
            {
                _logger.LogError(failEx, "Failed to report job failure to DataBridge for JobId: {JobId}", jobId);
            }

            // Cleanup local temp files
            CleanupLocalData(dataDir);
            throw;
        }
    }

    private async Task HandleExistingJobStateAsync(
        IJsMessageContext<FileDownloadRequest> context,
        FileDownloadRequest request,
        YtDlpMetadata metadata,
        string idempotencyKey,
        string? reason,
        Guid jobId)
    {
        var statusResponse = await TryGetJobStatusAsync(jobId);
        if (statusResponse == null)
        {
            throw new RetryLaterException(
                $"Unable to reconcile duplicate delivery for JobId {jobId} because status lookup failed.");
        }

        var status = JobStatusCodec.Parse(statusResponse.Status);
        if (status == JobStatus.UploadedPendingCommit
            && !string.IsNullOrWhiteSpace(statusResponse.StoragePath)
            && !string.IsNullOrWhiteSpace(statusResponse.FileHash))
        {
            _logger.LogWarning(
                "JobId {JobId} redelivered while awaiting commit. Reissuing VideoCommit for {StoragePath}.",
                jobId,
                statusResponse.StoragePath);

            try
            {
                var commitResponse = await ExecuteObservedPhaseAsync(
                    "reconcile_commit",
                    jobId,
                    () => ExecuteWithInProgressHeartbeatsAsync(
                        context,
                        phase: "reconcile-commit",
                        async () => await _resiliencePipeline.ExecuteAsync(async token =>
                        {
                            return await _messageBus.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
                                Subjects.VideoCommit,
                                new VideoCommitRequest(
                                    jobId,
                                    idempotencyKey,
                                    request.StorageKey,
                                    statusResponse.StoragePath!,
                                    statusResponse.FileHash!,
                                    metadata.RawJson,
                                    metadata.Platform,
                                    metadata.SourceLastModified),
                                NatsTimeout,
                                token);
                        })));

                if (commitResponse == null || !commitResponse.Success)
                {
                    var error = commitResponse?.ErrorMessage ?? "null response";
                    throw new InvalidOperationException(
                        $"DataBridge rejected reconcile commit for JobId {jobId}: {error}");
                }

                _logger.LogInformation("Reconciled pending commit for JobId: {JobId}", jobId);
                return;
            }
            catch (Exception ex)
            {
                throw new RetryLaterException(
                    $"Pending commit reconciliation failed for JobId {jobId}.",
                    ex);
            }
        }

        _logger.LogInformation(
            "DataBridge says skip for JobId: {JobId}. Reason: {Reason}. Status={Status}, Phase={Phase}, SubStatus={SubStatus}",
            jobId,
            reason,
            statusResponse.Status,
            statusResponse.Phase,
            statusResponse.SubStatus);
    }

    private async Task MarkUploadedPendingCommitAsync(Guid jobId, string storagePath, string fileHash)
    {
        var response = await _messageBus.RequestAsync<JobProgressRequest, JobProgressResponse>(
            Subjects.JobProgress,
            new JobProgressRequest(
                jobId,
                JobStatus.UploadedPendingCommit.ToStorageValue(),
                storagePath,
                fileHash),
            NatsTimeout);

        if (response == null || !response.Success)
        {
            var error = response?.ErrorMessage ?? "null response";
            throw new InvalidOperationException(
                $"Failed to mark job as UploadedPendingCommit for JobId {jobId}: {error}");
        }
    }

    private async Task<JobStatusResponse?> TryGetJobStatusAsync(Guid jobId)
    {
        try
        {
            return await _messageBus.RequestAsync<JobStatusRequest, JobStatusResponse>(
                Subjects.JobStatus,
                new JobStatusRequest(jobId),
                NatsTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query current job status for JobId {JobId} during compensation check.",
                jobId);
            return null;
        }
    }

    private async Task<T> ExecuteWithInProgressHeartbeatsAsync<T>(
        IJsMessageContext<FileDownloadRequest> context,
        string phase,
        Func<Task<T>> operation)
    {
        await TrySendInProgressHeartbeatAsync(context, phase);

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeatTask = RunHeartbeatLoopAsync(context, phase, heartbeatCts.Token);

        try
        {
            return await operation();
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ExecuteWithInProgressHeartbeatsAsync(
        IJsMessageContext<FileDownloadRequest> context,
        string phase,
        Func<Task> operation)
    {
        await ExecuteWithInProgressHeartbeatsAsync<object?>(
            context,
            phase,
            async () =>
            {
                await operation();
                return null;
            });
    }

    private async Task<T> ExecuteObservedPhaseAsync<T>(
        string phase,
        Guid jobId,
        Func<Task<T>> operation)
    {
        using var activity = SagaActivitySource.StartActivity($"froststream.saga.{phase}", ActivityKind.Internal);
        activity?.SetTag("job.id", jobId);
        activity?.SetTag("saga.phase", phase);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await operation();
            RecordPhaseMetrics(phase, "success", startedAt);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            RecordPhaseMetrics(phase, "error", startedAt);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task ExecuteObservedPhaseAsync(
        string phase,
        Guid jobId,
        Func<Task> operation)
    {
        await ExecuteObservedPhaseAsync<object?>(
            phase,
            jobId,
            async () =>
            {
                await operation();
                return null;
            });
    }

    private static void RecordPhaseMetrics(string phase, string outcome, long startedAt)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        SagaPhaseCounter.Add(1,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("outcome", outcome));

        SagaPhaseDuration.Record(elapsedMs,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    private async Task RunHeartbeatLoopAsync(
        IJsMessageContext<FileDownloadRequest> context,
        string phase,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(InProgressHeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await TrySendInProgressHeartbeatAsync(context, phase, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TrySendInProgressHeartbeatAsync(
        IJsMessageContext<FileDownloadRequest> context,
        string phase,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await context.InProgressAsync(cancellationToken);
            _logger.LogDebug("Sent in-progress heartbeat for JobId: {JobId} during {Phase}",
                context.Message.JobId, phase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send in-progress heartbeat for JobId: {JobId} during {Phase}",
                context.Message.JobId,
                phase);
        }
    }

    private void CleanupLocalData(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogDebug("Cleaned up local data directory: {Dir}", dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up local data directory: {Dir}", dir);
        }
    }

    private sealed class RetryLaterException : Exception
    {
        public RetryLaterException(string message)
            : base(message)
        {
        }

        public RetryLaterException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
