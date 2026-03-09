using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared.Messages;

namespace Shared.Storage;

/// <summary>
/// NATS-based implementation of <see cref="IJobCoordinationClient"/>.
/// Encapsulates subject names and communication patterns with DataBridge.
/// </summary>
public class NatsJobCoordinationClient : IJobCoordinationClient
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<NatsJobCoordinationClient> _logger;
    private readonly TimeSpan _defaultTimeout;

    // Subject constants - centralized here to decouple from Worker
    private const string JobStartSubject = "databridge.job.start";
    private const string JobProgressSubject = "databridge.job.progress";
    private const string VideoCommitSubject = "databridge.video.commit";
    private const string JobFailSubject = "databridge.job.fail";
    private const string JobStatusSubject = "databridge.job.status";

    public NatsJobCoordinationClient(
        IMessageBus messageBus,
        ILogger<NatsJobCoordinationClient> logger,
        TimeSpan? defaultTimeout = null)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<JobStartResponse> StartJobAsync(JobStartRequest request, CancellationToken ct)
    {
        _logger.LogDebug("Starting job {JobId} with idempotency key {IdempotencyKey}",
            request.JobId, request.IdempotencyKey);

        var response = await _messageBus.RequestAsync<JobStartRequest, JobStartResponse>(
            JobStartSubject,
            request,
            _defaultTimeout,
            ct);

        if (response == null)
        {
            throw new InvalidOperationException($"DataBridge returned null for job start (JobId: {request.JobId})");
        }

        return response;
    }

    public async Task<JobProgressResponse> ReportProgressAsync(JobProgressRequest request, CancellationToken ct)
    {
        var response = await _messageBus.RequestAsync<JobProgressRequest, JobProgressResponse>(
            JobProgressSubject,
            request,
            _defaultTimeout,
            ct);

        if (response == null)
        {
            throw new InvalidOperationException($"DataBridge returned null for job progress (JobId: {request.JobId})");
        }

        return response;
    }

    public async Task<VideoCommitResponse> CommitVideoAsync(VideoCommitRequest request, CancellationToken ct)
    {
        _logger.LogDebug("Committing video for job {JobId}", request.JobId);

        var response = await _messageBus.RequestAsync<VideoCommitRequest, VideoCommitResponse>(
            VideoCommitSubject,
            request,
            _defaultTimeout,
            ct);

        return response ?? new VideoCommitResponse(false, "null response");
    }

    public async Task ReportFailureAsync(JobFailRequest request, CancellationToken ct)
    {
        try
        {
            await _messageBus.RequestAsync<JobFailRequest, JobFailResponse>(
                JobFailSubject,
                request,
                _defaultTimeout,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report job failure to DataBridge for JobId: {JobId}", request.JobId);
            throw;
        }
    }

    public async Task<JobStatusResponse?> GetStatusAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            return await _messageBus.RequestAsync<JobStatusRequest, JobStatusResponse>(
                JobStatusSubject,
                new JobStatusRequest(jobId),
                _defaultTimeout,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query job status for JobId {JobId}", jobId);
            return null;
        }
    }
}
