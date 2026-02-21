using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Shared;
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
                _logger.LogError("DataBridge returned null for job.start (JobId: {JobId})", jobId);
                await context.NackAsync(delay: TimeSpan.FromSeconds(30));
                return;
            }

            if (!startResponse.Proceed)
            {
                _logger.LogInformation("DataBridge says skip for JobId: {JobId}. Reason: {Reason}",
                    jobId, startResponse.Reason);
                await context.AckAsync();
                return;
            }

            // ── Phase 3: Download the video file locally ────────────────────
            var downloadResult = await _ytDlp.DownloadAsync(request.Url, dataDir);

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

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process download for JobId: {JobId}", jobId);

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
                await _messageBus.RequestAsync<JobFailRequest, object>(
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

            // Nak with delay so it can be retried
            await context.NackAsync(delay: TimeSpan.FromSeconds(60));
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
