using System.Text.Json;
using Conduit.NATS;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Downloads.Models;

namespace WebAPI.Features.Downloads.Controllers;

/// <summary>
/// Admin-focused download queue / history surface. Read routes expose the full download-job history
/// with filters, per-job detail, and per-job event timelines; live routes stream progress over SSE;
/// control routes are queue-oriented aliases for the existing priority/cancel/restart operations.
/// </summary>
[ApiController]
[Route("api/downloads/queue")]
public sealed class DownloadQueueController(
    IMessageBus messageBus,
    DownloadQueueHub hub,
    ILogger<DownloadQueueController> logger) : ControllerBase
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AdminRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Read surface ─────────────────────────────────────────────────────────────────

    [HttpGet]
    [Endpoint(EndpointIds.DownloadsQueueList)]
    [EndpointSummary("List the download queue / history")]
    [EndpointDescription("Returns a filtered, paginated slice of the full download-job history from the authoritative DataBridge read model. Supports filtering by state, source kind, requester, storage key, creation time range, and source-URL text, plus newest-first or priority sort. Progress is not included because it is delivered live-only over the SSE routes.")]
    public async Task<ActionResult<DownloadQueueListResponse>> List(
        [FromQuery] DownloadJobState? state,
        [FromQuery] DownloadSourceKind? sourceKind,
        [FromQuery] string? requestedBy,
        [FromQuery] string? storageKey,
        [FromQuery] DateTimeOffset? createdFrom,
        [FromQuery] DateTimeOffset? createdTo,
        [FromQuery] string? q,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        [FromQuery] string sort = "createdAt",
        CancellationToken cancellationToken = default)
    {
        var normalizedSort = sort.Trim().ToLowerInvariant() switch
        {
            "priority" => DownloadQueueSort.Priority,
            "createdat" or "" => DownloadQueueSort.CreatedAtDesc,
            _ => (DownloadQueueSort?)null
        };
        if (normalizedSort is null)
            return BadRequest(new ProblemDetails { Title = "Query parameter 'sort' must be 'createdAt' or 'priority'.", Status = StatusCodes.Status400BadRequest });

        var request = new DownloadQueueListRequest
        {
            State = state,
            SourceKind = sourceKind,
            RequestedBy = requestedBy,
            StorageKey = storageKey,
            CreatedFrom = createdFrom is { } from ? Instant.FromDateTimeOffset(from) : null,
            CreatedTo = createdTo is { } to ? Instant.FromDateTimeOffset(to) : null,
            Query = q,
            Sort = normalizedSort.Value,
            Limit = limit,
            Cursor = cursor
        };

        var response = await SendQueryAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
            DownloadQueueSubjects.List, request, cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return QueueError(response.ErrorCode, response.ErrorMessage);

        return Ok(response);
    }

    [HttpGet("{jobId:guid}")]
    [Endpoint(EndpointIds.DownloadsQueueGet)]
    [EndpointSummary("Get a download job's queue detail")]
    [EndpointDescription("Returns the queue snapshot for a single download job, including state, source, requester, storage key, attempts, file size/hash, failure info, and provider-halt retry scheduling. Returns 404 when the job does not exist. Progress is not included because it is delivered live-only over the SSE routes.")]
    public async Task<ActionResult<DownloadQueueJobDto>> Get(Guid jobId, CancellationToken cancellationToken)
    {
        var response = await SendQueryAsync<DownloadQueueGetRequest, DownloadQueueGetResponse>(
            DownloadQueueSubjects.Get, new DownloadQueueGetRequest { JobId = jobId }, cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return QueueError(response.ErrorCode, response.ErrorMessage);
        if (response.Job is null)
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Title = "DataBridge returned an invalid job response.", Status = StatusCodes.Status502BadGateway });

        return Ok(response.Job);
    }

    [HttpGet("{jobId:guid}/history")]
    [Endpoint(EndpointIds.DownloadsQueueHistory)]
    [EndpointSummary("Get a download job's persisted event timeline")]
    [EndpointDescription("Returns the persisted download_job_history timeline for a single job, ordered by recorded time and id. Each entry carries the event name, operation key, message id, optional payload JSON, and recorded instant. Returns 404 when the job does not exist.")]
    public async Task<ActionResult<IReadOnlyList<DownloadQueueHistoryEntryDto>>> History(Guid jobId, CancellationToken cancellationToken)
    {
        var response = await SendQueryAsync<DownloadQueueHistoryRequest, DownloadQueueHistoryResponse>(
            DownloadQueueSubjects.History, new DownloadQueueHistoryRequest { JobId = jobId }, cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return QueueError(response.ErrorCode, response.ErrorMessage);

        return Ok(response.Entries);
    }

    // ── Live surface (SSE) ────────────────────────────────────────────────────────────

    [HttpGet("stream")]
    [Endpoint(EndpointIds.DownloadsQueueStream)]
    [EndpointSummary("Stream queue-wide download progress via SSE")]
    [EndpointDescription("Opens a Server-Sent Events stream that delivers live yt-dlp progress for every download job. Events are emitted as 'data: {json}' lines carrying the job id and progress snapshot. Progress is live-only: a new subscriber receives events from the moment it connects onward, with no replay. The stream does not auto-close; clients disconnect when finished.")]
    public Task StreamQueueAsync(CancellationToken cancellationToken)
    {
        var (id, reader) = hub.SubscribeToQueue();
        return StreamAsync(id, reader, cancellationToken);
    }

    [HttpGet("{jobId:guid}/progress")]
    [Endpoint(EndpointIds.DownloadsQueueProgress)]
    [EndpointSummary("Stream a single job's download progress via SSE")]
    [EndpointDescription("Opens a Server-Sent Events stream that delivers live yt-dlp progress for one download job. Events are emitted as 'data: {json}' lines using the same shape as the queue-wide stream. Progress is live-only with no replay. The stream does not auto-close on job completion — clients should disconnect when the job reaches a terminal state.")]
    public Task StreamJobProgressAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var (id, reader) = hub.SubscribeToJob(jobId);
        return StreamAsync(id, reader, cancellationToken);
    }

    private async Task StreamAsync(Guid subscriptionId, System.Threading.Channels.ChannelReader<DownloadProgress> reader, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        var feature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        feature?.DisableBuffering();

        try
        {
            await foreach (var progress in reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(MapToEvent(progress), JsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            hub.Unsubscribe(subscriptionId);
        }
    }

    // ── Control aliases (reuse existing DataBridge request/reply behavior) ──────────────

    [HttpPatch("{jobId:guid}/priority")]
    [Endpoint(EndpointIds.DownloadsQueuePriority)]
    [EndpointSummary("Update a queued download job's priority")]
    [EndpointDescription("Queue-oriented alias for changing the scheduling priority (0–100) of a queued download job. Higher values run before lower ones. Effective only while the job is waiting for a download slot; no-ops if the download has already started.")]
    public async Task<ActionResult> UpdatePriority(
        [FromRoute] Guid jobId,
        [FromBody] UpdatePriorityRequest request,
        CancellationToken cancellationToken)
    {
        UpdateDownloadPriorityResponse? response;
        try
        {
            response = await messageBus.RequestAsync<UpdateDownloadPriorityRequest, UpdateDownloadPriorityResponse>(
                DownloadSubjects.UpdatePriorityRequest,
                new UpdateDownloadPriorityRequest { JobId = jobId, Priority = request.Priority },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting priority update for JobId {JobId}", jobId);
            return BadGateway("Failed to update priority", "Could not reach the messaging bus.");
        }

        if (response is null || !response.Success)
        {
            var error = response?.Error ?? "No response from DataBridge.";
            if (error == "Job not found.")
                return NotFound(new ProblemDetails { Title = "Job not found.", Status = StatusCodes.Status404NotFound });
            return BadRequest(new ProblemDetails { Title = error, Status = StatusCodes.Status400BadRequest });
        }

        return NoContent();
    }

    [HttpPost("{jobId:guid}/cancel")]
    [Endpoint(EndpointIds.DownloadsQueueCancel)]
    [EndpointSummary("Cancel a download job")]
    [EndpointDescription("Queue-oriented alias for requesting clean cancellation of a queued or active download job. Queued jobs are removed from the scheduler; active yt-dlp processes are asked to stop and any worker-local temp files are cleaned.")]
    public async Task<ActionResult> Cancel(
        [FromRoute] Guid jobId,
        [FromBody] CancelDownloadApiRequest? request,
        CancellationToken cancellationToken)
    {
        CancelDownloadResponse? response;
        try
        {
            response = await messageBus.RequestAsync<CancelDownloadRequest, CancelDownloadResponse>(
                DownloadSubjects.CancelDownloadRequest,
                new CancelDownloadRequest
                {
                    JobId = jobId,
                    RequestedBy = AuthConstants.FindSubject(User),
                    Reason = request?.Reason
                },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting cancellation for JobId {JobId}", jobId);
            return BadGateway("Failed to cancel download", "Could not reach the messaging bus.");
        }

        if (response is null)
            return BadGateway("Failed to cancel download", "No response from DataBridge.");

        if (response.Success)
            return Accepted(new CancelDownloadApiResponse(response.State ?? DownloadJobState.Cancelling));

        if (response.Error == "Job not found.")
            return NotFound(new ProblemDetails { Title = "Job not found.", Status = StatusCodes.Status404NotFound });

        return Conflict(new ProblemDetails
        {
            Title = response.Error ?? "Download cannot be cancelled.",
            Status = StatusCodes.Status409Conflict
        });
    }

    [HttpPost("{jobId:guid}/restart")]
    [Endpoint(EndpointIds.DownloadsQueueRestart)]
    [EndpointSummary("Restart a halted download job")]
    [EndpointDescription("Queue-oriented alias for restarting a download job that was halted by provider bot-detection. The original request is replayed and the worker resumes from the last recorded successful step when possible.")]
    public async Task<ActionResult> RestartHalted(
        [FromRoute] Guid jobId,
        CancellationToken cancellationToken)
    {
        RestartHaltedDownloadResponse? response;
        try
        {
            response = await messageBus.RequestAsync<RestartHaltedDownloadRequest, RestartHaltedDownloadResponse>(
                DownloadSubjects.RestartHaltedDownloadRequest,
                new RestartHaltedDownloadRequest
                {
                    JobId = jobId,
                    RequestedBy = AuthConstants.FindSubject(User)
                },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting restart for JobId {JobId}", jobId);
            return BadGateway("Failed to restart download", "Could not reach the messaging bus.");
        }

        if (response is null)
            return BadGateway("Failed to restart download", "No response from DataBridge.");

        if (response.Success)
            return Accepted(new { State = "queued", JobId = response.JobId });

        return response.ErrorCode switch
        {
            "not_halted" => Conflict(new ProblemDetails { Title = response.ErrorMessage, Status = StatusCodes.Status409Conflict }),
            "missing_request" => StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = response.ErrorMessage, Status = StatusCodes.Status500InternalServerError }),
            _ => StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
            {
                Title = response.ErrorMessage ?? "Download cannot be restarted.",
                Status = StatusCodes.Status409Conflict
            })
        };
    }

    private async Task<TResponse?> SendQueryAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(subject, request, QueryTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing download queue request on subject {Subject}", subject);
            return null;
        }
    }

    private ObjectResult QueueError(string? errorCode, string? errorMessage)
        => errorCode switch
        {
            "not_found" => NotFound(new ProblemDetails { Title = errorMessage ?? "Download job was not found.", Status = StatusCodes.Status404NotFound }),
            "validation" => BadRequest(new ProblemDetails { Title = errorMessage ?? "Invalid download queue request.", Status = StatusCodes.Status400BadRequest }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails { Title = errorMessage ?? "Download queue query failed.", Status = StatusCodes.Status502BadGateway })
        };

    private ObjectResult BadGateway(string title = "DataBridge is unreachable.", string? detail = null)
        => StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status502BadGateway
        });

    private static ProgressEvent MapToEvent(DownloadProgress p) => new(
        p.JobId,
        p.Sequence,
        p.Phase,
        p.Percent,
        p.DownloadedBytes,
        p.TotalBytes,
        p.Speed,
        p.EtaSeconds,
        p.Message);

    private sealed record ProgressEvent(
        Guid JobId,
        int Sequence,
        string Phase,
        double? Percent,
        long? DownloadedBytes,
        long? TotalBytes,
        string? Speed,
        double? EtaSeconds,
        string? Message);
}
