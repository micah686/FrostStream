using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conduit.NATS;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Downloads.Models;

namespace WebAPI.Features.Downloads.Controllers;

/// <summary>
/// Admin-focused download queue / history surface. Read routes expose the full download-job history
/// with filters, per-job detail, and per-job event timelines; live routes stream progress over SSE;
/// V2 control routes explicitly start fresh runs, stop active work, control groups, and clear
/// persistent provider circuits.
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
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // Match the MVC JSON contract (camelCase, string enums, ISO NodaTime) so SSE payloads are
        // shaped exactly like the REST DTOs the client also consumes.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    // ── Read surface ─────────────────────────────────────────────────────────────────

    [HttpGet]
    [Endpoint(EndpointIds.DownloadsQueueList)]
    [EndpointSummary("List the download queue / history")]
    [EndpointDescription("Returns a filtered, paginated slice of the full download-job history from the authoritative DataBridge read model. Supports filtering by V2 status, source kind, requester, storage key, creation time range, and source-URL text, plus newest-first or priority sort. Progress is not included because it is delivered live-only over the SSE routes.")]
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
        [FromQuery] DownloadQueueStateGroup stateGroup = DownloadQueueStateGroup.All,
        [FromQuery] DownloadJobStatus? status = null,
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
            Status = status,
            StateGroup = stateGroup,
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
    [EndpointDescription("Returns the queue snapshot for a single download job, including V2 status, stage, immutable run identity, attempt, artifact, warnings, source, storage, and last-failure details. Returns 404 when the job does not exist. Progress is not included because it is delivered live-only over the SSE routes.")]
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

    [HttpGet("{jobId:guid}/media")]
    [Endpoint(EndpointIds.DownloadsQueueMedia)]
    [EndpointSummary("Resolve a download job's produced media")]
    [EndpointDescription("Resolves the media item a completed job produced via media_source_versions.latest_job_id, for deep-linking to /watch/{mediaGuid}. Returns 404 when the job never produced media or is no longer the latest job for its source (a later re-download of the same source overwrites latest_job_id).")]
    public async Task<ActionResult<DownloadQueueMediaDto>> GetMedia(Guid jobId, CancellationToken cancellationToken)
    {
        var response = await SendQueryAsync<DownloadQueueMediaRequest, DownloadQueueMediaResponse>(
            DownloadQueueSubjects.Media, new DownloadQueueMediaRequest { JobId = jobId }, cancellationToken);

        if (response is null)
            return BadGateway();
        if (!response.Success)
            return QueueError(response.ErrorCode, response.ErrorMessage);
        if (response.MediaGuid is null)
            return NotFound(new ProblemDetails { Title = "No media resolved for this job.", Status = StatusCodes.Status404NotFound });

        return Ok(new DownloadQueueMediaDto(response.MediaGuid.Value));
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

    private async Task StreamAsync(Guid subscriptionId, ChannelReader<QueueStreamEvent> reader, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        var feature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        feature?.DisableBuffering();

        // Serialize all writes to the response body — the heartbeat timer and the event loop both write.
        var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = SendHeartbeatsAsync(writeLock, heartbeatCts.Token);

        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken))
            {
                var (name, json) = Serialize(evt);
                await WriteFrameAsync(writeLock, $"event: {name}\ndata: {json}\n\n", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { /* best-effort cleanup */ }
            hub.Unsubscribe(subscriptionId);
            writeLock.Dispose();
        }
    }

    private async Task SendHeartbeatsAsync(SemaphoreSlim writeLock, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                // SSE comment line — keeps intermediaries from idling the connection out.
                await WriteFrameAsync(writeLock, ": keepalive\n\n", ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Client went away mid-write; the main loop's cancellation handles teardown.
                return;
            }
        }
    }

    private async Task WriteFrameAsync(SemaphoreSlim writeLock, string frame, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await Response.WriteAsync(frame, ct);
            await Response.Body.FlushAsync(ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static (string Name, string Json) Serialize(QueueStreamEvent evt) => evt switch
    {
        QueueStreamEvent.Progress p => ("progress", JsonSerializer.Serialize(MapProgress(p.Value), JsonOptions)),
        QueueStreamEvent.State s => ("state", JsonSerializer.Serialize(MapState(s.Value), JsonOptions)),
        _ => throw new InvalidOperationException($"Unsupported queue stream event {evt.GetType().Name}.")
    };

    private static ProgressEvent MapProgress(DownloadProgress p) => new(
        p.JobId,
        p.Execution?.RunId,
        p.Execution?.Stage,
        p.Execution?.Attempt ?? p.Attempt,
        p.Sequence,
        p.Phase,
        p.Percent,
        p.DownloadedBytes,
        p.TotalBytes,
        p.Speed,
        p.EtaSeconds,
        p.Message);

    private static StateEvent MapState(DownloadQueueStateChanged s) => new(
        s.JobId,
        s.Status,
        s.PreviousStatus,
        s.Stage,
        s.StageStatus,
        s.RunId,
        s.RunNumber,
        s.Attempt,
        s.ArtifactKey,
        s.WarningCount,
        s.OccurredAt);

    private sealed record ProgressEvent(
        Guid JobId,
        Guid? RunId,
        DownloadStage? Stage,
        int Attempt,
        int Sequence,
        string Phase,
        double? Percent,
        long? DownloadedBytes,
        long? TotalBytes,
        string? Speed,
        double? EtaSeconds,
        string? Message);

    private sealed record StateEvent(
        Guid JobId,
        DownloadJobStatus Status,
        DownloadJobStatus PreviousStatus,
        DownloadStage Stage,
        DownloadStageStatus StageStatus,
        Guid? RunId,
        int RunNumber,
        int Attempt,
        string? ArtifactKey,
        int WarningCount,
        Instant OccurredAt);

    // ── V2 controls ───────────────────────────────────────────────────────────────────

    [HttpPatch("{jobId:guid}/priority")]
    [Endpoint(EndpointIds.DownloadsQueuePriority)]
    [EndpointSummary("Update a download job's priority")]
    [EndpointDescription("Changes the stored administrative priority (0–100) used by priority-sorted queue views. This operation never starts, resumes, or interrupts a V2 run.")]
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

    [HttpPost("{jobId:guid}/stop")]
    [Endpoint(EndpointIds.DownloadsQueueStop)]
    [EndpointSummary("Stop a download job")]
    [EndpointDescription("Stops a queued or active download run, blocks further stage dispatches, cancels active worker work, and compensates any partial durable artifacts before settling as Stopped.")]
    public async Task<ActionResult> Stop(
        [FromRoute] Guid jobId,
        [FromBody] StopDownloadApiRequest? request,
        CancellationToken cancellationToken)
    {
        StopDownloadResponse? response;
        try
        {
            response = await messageBus.RequestAsync<StopDownloadRequest, StopDownloadResponse>(
                DownloadSubjects.StopDownloadRequest,
                new StopDownloadRequest
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
            logger.LogError(ex, "Failed requesting stop for JobId {JobId}", jobId);
            return BadGateway("Failed to stop download", "Could not reach the messaging bus.");
        }

        if (response is null)
            return BadGateway("Failed to stop download", "No response from DataBridge.");

        if (response.Success)
            return Accepted(new StopDownloadApiResponse(response.Status ?? DownloadJobStatus.Stopping));

        if (response.ErrorCode == "not_found")
            return NotFound(new ProblemDetails { Title = "Job not found.", Status = StatusCodes.Status404NotFound });

        return Conflict(new ProblemDetails
        {
            Title = response.ErrorMessage ?? "Download cannot be stopped.",
            Status = StatusCodes.Status409Conflict
        });
    }

    [HttpPost("{jobId:guid}/start")]
    [Endpoint(EndpointIds.DownloadsQueueStart)]
    [EndpointSummary("Start a download job")]
    [EndpointDescription("Starts a Stopped or Failed download job from metadata with a fresh RunId while retaining prior run history and resetting stage attempts and temporary working references.")]
    public async Task<ActionResult> Start(
        [FromRoute] Guid jobId,
        CancellationToken cancellationToken)
    {
        StartDownloadResponse? response;
        try
        {
            response = await messageBus.RequestAsync<StartDownloadRequest, StartDownloadResponse>(
                DownloadSubjects.StartDownloadRequest,
                new StartDownloadRequest
                {
                    JobId = jobId,
                    RequestedBy = AuthConstants.FindSubject(User)
                },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed requesting start for JobId {JobId}", jobId);
            return BadGateway("Failed to start download", "Could not reach the messaging bus.");
        }

        if (response is null)
            return BadGateway("Failed to start download", "No response from DataBridge.");

        if (response.Success)
            return Accepted(new { Status = "running", JobId = response.JobId, RunId = response.RunId });

        return Conflict(new ProblemDetails
        {
            Title = response.ErrorMessage ?? "Download cannot be started.",
            Status = StatusCodes.Status409Conflict
        });
    }

    [HttpPost("/api/downloads/groups/{correlationId:guid}/start")]
    [Endpoint(EndpointIds.DownloadsGroupStart)]
    [EndpointSummary("Start eligible jobs in a download group")]
    [EndpointDescription("Starts each Stopped or Failed child in the identified playlist, channel, creator-monitor, or direct-download group as an independent fresh run; completed children remain unchanged.")]
    public async Task<ActionResult> StartGroup(Guid correlationId, CancellationToken cancellationToken)
    {
        var response = await messageBus.RequestAsync<StartDownloadGroupRequest, DownloadGroupControlResponse>(
            DownloadSubjects.StartGroupRequest,
            new StartDownloadGroupRequest
            {
                CorrelationId = correlationId,
                RequestedBy = AuthConstants.FindSubject(User)
            },
            AdminRequestTimeout,
            cancellationToken);
        return response is { Success: true }
            ? Accepted(response)
            : BadGateway("Failed to start download group", response?.ErrorMessage ?? "No response from DataBridge.");
    }

    [HttpPost("/api/downloads/groups/{correlationId:guid}/stop")]
    [Endpoint(EndpointIds.DownloadsGroupStop)]
    [EndpointSummary("Stop expansion and active jobs in a download group")]
    [EndpointDescription("Stops further collection expansion and requests a clean stop for every queued or active nonterminal child in the identified group, without altering already completed children.")]
    public async Task<ActionResult> StopGroup(
        Guid correlationId,
        [FromBody] StopDownloadApiRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await messageBus.RequestAsync<StopDownloadGroupRequest, DownloadGroupControlResponse>(
            DownloadSubjects.StopGroupRequest,
            new StopDownloadGroupRequest
            {
                CorrelationId = correlationId,
                RequestedBy = AuthConstants.FindSubject(User),
                Reason = request?.Reason
            },
            AdminRequestTimeout,
            cancellationToken);
        return response is { Success: true }
            ? Accepted(response)
            : BadGateway("Failed to stop download group", response?.ErrorMessage ?? "No response from DataBridge.");
    }

    [HttpPost("/api/downloads/providers/{provider}/circuit/clear")]
    [Endpoint(EndpointIds.DownloadsProviderCircuitClear)]
    [EndpointSummary("Clear a download provider circuit")]
    [EndpointDescription("Clears a provider-wide download halt. Stopped or failed jobs remain stopped until a user explicitly starts them.")]
    public async Task<ActionResult> ClearProviderCircuit(string provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 255)
            return BadRequest(new ProblemDetails { Title = "A valid provider is required.", Status = StatusCodes.Status400BadRequest });

        ClearProviderCircuitResponse? response;
        try
        {
            response = await messageBus.RequestAsync<ClearProviderCircuitRequest, ClearProviderCircuitResponse>(
                DownloadSubjects.ClearProviderCircuitRequest,
                new ClearProviderCircuitRequest
                {
                    Provider = provider,
                    RequestedBy = AuthConstants.FindSubject(User)
                },
                AdminRequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed clearing download provider circuit {Provider}", provider);
            return BadGateway("Failed to clear provider circuit", "Could not reach the messaging bus.");
        }

        return response is { Success: true }
            ? NoContent()
            : BadGateway("Failed to clear provider circuit", response?.ErrorMessage ?? "No response from DataBridge.");
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
}
