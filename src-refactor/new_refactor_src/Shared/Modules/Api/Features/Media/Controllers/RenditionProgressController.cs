using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Live SSE surface for MediaProcessor rendition encodes (stream/HLS and audio/Opus). Progress is
/// advisory and live-only — clients snapshot rendition status via the existing query endpoints and
/// treat these frames as deltas, exactly like the download queue's SSE surface.
/// </summary>
[ApiController]
[Route("api/media/renditions")]
public sealed class RenditionProgressController(RenditionProgressHub hub) : ControllerBase
{
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

    [HttpGet("progress/stream")]
    [Endpoint(EndpointIds.MediaRenditionsProgressStream)]
    [EndpointSummary("Stream rendition encode progress via SSE")]
    [EndpointDescription("Opens a Server-Sent Events stream that delivers live MediaProcessor encode progress for stream (HLS) and audio (Opus) renditions. Events are emitted as 'event: progress' frames whose data carries the rendition id, kind, media guid, phase, percent, speed, and ETA. Progress is live-only with no replay; pass mediaGuid to receive frames for a single media item only. The stream does not auto-close; clients disconnect when finished.")]
    public Task StreamAsync([FromQuery] Guid? mediaGuid, CancellationToken cancellationToken)
    {
        var (id, reader) = hub.Subscribe(mediaGuid);
        return StreamEventsAsync(id, reader, cancellationToken);
    }

    private async Task StreamEventsAsync(Guid subscriptionId, ChannelReader<RenditionProgress> reader, CancellationToken cancellationToken)
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
            await foreach (var frame in reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(frame, JsonOptions);
                await WriteFrameAsync(writeLock, $"event: progress\ndata: {json}\n\n", cancellationToken);
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
}
