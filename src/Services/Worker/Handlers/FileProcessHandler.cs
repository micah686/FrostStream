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

        _logger.LogInformation("Processing download request for JobId: {JobId}, Url: {Url}, StorageKey: {StorageKey}",
            jobId, request.Url, request.StorageKey);

        string? uploadedBlobPath = null;
        IBlobStorage? storage = null;
        var dataDir = Path.Combine(Path.GetTempPath(), "froststream", "data", jobId.ToString());

        try
        {
            // ── Phase 1: Pre-flight metadata fetch ──────────────────────────
            var metadata = await _ytDlp.FetchMetadataAsync(request.Url);
            var idempotencyKey = YtDlpService.ComputeIdempotencyKey(
                request.Url, request.StorageKey, metadata.SourceLastModified);

            _logger.LogInformation("Computed IdempotencyKey: {Key} for video {VideoId} ({Platform})",
                idempotencyKey, metadata.Id, metadata.Platform);

            // ── Phase 2: Idempotency & state tracking check via DataBridge ──
            var startResponse = await _messageBus.RequestAsync<JobStartRequest, JobStartResponse>(
                Subjects.JobStart,
                new JobStartRequest(jobId, idempotencyKey, request.StorageKey, request.Url),
                NatsTimeout);

            if (startResponse == null)
            {
                throw new InvalidOperationException(
                    $"DataBridge returned null for job.start (JobId: {jobId})");
            }

            if (!startResponse.Proceed)
            {
                _logger.LogInformation("DataBridge says skip for JobId: {JobId}. Reason: {Reason}",
                    jobId, startResponse.Reason);
                return;
            }

            // ── Phase 3: Download the video file locally ────────────────────
            var downloadResult = await ExecuteWithInProgressHeartbeatsAsync(
                context,
                phase: "download",
                () => _ytDlp.DownloadAsync(request.Url, dataDir));

            _logger.LogInformation("Downloaded video {VideoId}: {FilePath} (hash: {Hash})",
                downloadResult.Metadata.Id, downloadResult.LocalFilePath, downloadResult.FileHash);

            // ── Phase 4: Get storage config from DataBridge ─────────────────
            var storageCfg = await _messageBus.RequestAsync<StorageConfigRequest, StorageConfigResponse>(
                Subjects.StorageConfig,
                new StorageConfigRequest(request.StorageKey),
                NatsTimeout);

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

            await _resiliencePipeline.ExecuteAsync(async token =>
            {
                await using var fs = File.OpenRead(downloadResult.LocalFilePath);
                await storage.WriteAsync(uploadedBlobPath, fs, cancellationToken: token);
            });

            _logger.LogInformation("Upload complete for {Path}", uploadedBlobPath);

            // Explicit saga transition: artifact is uploaded, awaiting DB commit.
            await MarkUploadedPendingCommitAsync(jobId, uploadedBlobPath, downloadResult.FileHash);

            // ── Phase 6: Atomic commit via DataBridge ────────────────────────
            var commitResponse = await _resiliencePipeline.ExecuteAsync(async token =>
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
            });

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
            _logger.LogError(ex, "Failed to process download for JobId: {JobId}", jobId);

            // C2: if commit state is uncertain, query DataBridge before compensating storage.
            if (uploadedBlobPath != null)
            {
                if (await IsCommitAlreadyCompletedAsync(jobId))
                {
                    _logger.LogWarning(
                        "Detected completed commit for JobId {JobId} after exception path. Skipping rollback and JobFail.",
                        jobId);
                    CleanupLocalData(dataDir);
                    return;
                }
            }

            // ── Compensating transaction: rollback uploaded file ─────────────
            if (storage != null && uploadedBlobPath != null)
            {
                try
                {
                    _logger.LogWarning("Rolling back: deleting uploaded blob {Path}", uploadedBlobPath);
                    await storage.DeleteAsync(new[] { uploadedBlobPath });
                }
                catch (Exception rollbackEx)
                {
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

    private async Task<JobStatus?> TryGetCurrentJobStatusAsync(Guid jobId)
    {
        try
        {
            var response = await _messageBus.RequestAsync<JobStatusRequest, JobStatusResponse>(
                Subjects.JobStatus,
                new JobStatusRequest(jobId),
                NatsTimeout);

            return response == null
                ? null
                : JobStatusCodec.Parse(response.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to query current job status for JobId {JobId} during compensation check.",
                jobId);
            return null;
        }
    }

    private async Task<bool> IsCommitAlreadyCompletedAsync(Guid jobId)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var status = await TryGetCurrentJobStatusAsync(jobId);
            if (status == JobStatus.Completed)
            {
                return true;
            }

            if (attempt == maxAttempts)
            {
                return false;
            }

            if (status is JobStatus.UploadedPendingCommit or JobStatus.Processing)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            return false;
        }

        return false;
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
}
