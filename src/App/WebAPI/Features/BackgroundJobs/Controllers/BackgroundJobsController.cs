using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.BackgroundJobs.Controllers;

/// <summary>
/// Live view of scheduled/background task executions. Everything here is served from the in-memory
/// <see cref="BackgroundJobHub"/> and is intentionally volatile: the list only covers runs this
/// server process observed, so it empties on restart. The durable per-schedule outcome (last run,
/// last success, next due) belongs to the Schedules admin surface.
/// </summary>
[ApiController]
[Route("api/jobs/background")]
public sealed class BackgroundJobsController(BackgroundJobHub hub) : ControllerBase
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [HttpGet]
    [Endpoint(EndpointIds.JobsBackgroundList)]
    [EndpointSummary("List in-progress and recently finished background tasks")]
    [EndpointDescription("Returns the scheduled/background task runs this server process has observed: everything currently running, plus a short tail of recently finished runs for context. In-memory and live-only — the list is empty immediately after a server restart even if schedules ran earlier. Each run carries its captured progress log so a client can render detail without opening the stream.")]
    public ActionResult<BackgroundRunListResponse> List()
    {
        var items = hub.List().Select(Map).ToArray();
        return Ok(new BackgroundRunListResponse(items, items.Count(x => x.Status == "running")));
    }

    [HttpGet("stream")]
    [Endpoint(EndpointIds.JobsBackgroundStream)]
    [EndpointSummary("Stream background task activity via SSE")]
    [EndpointDescription("Opens a Server-Sent Events stream carrying 'started', 'progress', and 'completed' events for scheduled/background task runs. Live-only with no replay: clients snapshot via GET /api/jobs/background on connect and then apply stream events. The stream does not auto-close.")]
    public async Task StreamAsync(CancellationToken cancellationToken)
    {
        var (subscriptionId, reader) = hub.Subscribe();

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

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
                await WriteFrameAsync(writeLock, ": keepalive\n\n", ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
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

    private static (string Name, string Json) Serialize(BackgroundRunStreamEvent evt) => evt switch
    {
        BackgroundRunStreamEvent.Started s => ("started", JsonSerializer.Serialize(Map(s.Value), JsonOptions)),
        BackgroundRunStreamEvent.Completed c => ("completed", JsonSerializer.Serialize(Map(c.Value), JsonOptions)),
        BackgroundRunStreamEvent.Progress p => ("progress", JsonSerializer.Serialize(
            new BackgroundRunProgressDto(p.Value.RunId, p.Value.Message, p.Value.Current, p.Value.Total,
                p.Value.Percent, p.Value.OccurredAt), JsonOptions)),
        _ => throw new InvalidOperationException($"Unsupported background run event {evt.GetType().Name}.")
    };

    private static BackgroundRunDto Map(BackgroundRunView run) => new(
        run.RunId,
        run.TaskType,
        run.ScheduleKey,
        run.Trigger,
        run.Origin,
        run.Detail,
        run.Status,
        run.Message,
        run.Current,
        run.Total,
        run.Percent,
        run.StartedAt,
        run.CompletedAt,
        run.ErrorMessage,
        run.Summary,
        run.Log.Select(x => new BackgroundRunLogDto(x.Message, x.At)).ToArray());
}

public sealed record BackgroundRunListResponse(IReadOnlyList<BackgroundRunDto> Items, int RunningCount);

public sealed record BackgroundRunDto(
    Guid RunId,
    string TaskType,
    string? ScheduleKey,
    BackgroundRunTrigger Trigger,
    string Origin,
    string? Detail,
    string Status,
    string? Message,
    int? Current,
    int? Total,
    double? Percent,
    Instant StartedAt,
    Instant? CompletedAt,
    string? ErrorMessage,
    string? Summary,
    IReadOnlyList<BackgroundRunLogDto> Log);

public sealed record BackgroundRunLogDto(string Message, Instant At);

public sealed record BackgroundRunProgressDto(
    Guid RunId, string Message, int? Current, int? Total, double? Percent, Instant OccurredAt);
