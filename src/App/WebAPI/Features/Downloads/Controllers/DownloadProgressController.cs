using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Downloads.Controllers;

[ApiController]
[Route("api/downloads")]
public sealed class DownloadProgressController(DownloadProgressHub hub) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Streams real-time yt-dlp download progress for a specific job as Server-Sent Events.
    /// Each event is a JSON-encoded progress snapshot. The stream stays open until the client
    /// disconnects; clients should disconnect once the job reaches a terminal state.
    /// </summary>
    [HttpGet("{jobId:guid}/progress")]
    [Endpoint(EndpointIds.DownloadsProgress)]
    [EndpointSummary("Stream download progress via SSE")]
    [EndpointDescription("Opens a Server-Sent Events stream that delivers live yt-dlp progress for the given job. Events are emitted as 'data: {json}' lines. The stream does not auto-close on job completion — clients should disconnect when the download job reaches a terminal state.")]
    public async Task StreamProgressAsync(Guid jobId, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        var feature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        feature?.DisableBuffering();

        var reader = hub.Subscribe(jobId);
        try
        {
            await foreach (var progress in reader.ReadAllAsync(cancellationToken))
            {
                var evt = MapToEvent(progress);
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            hub.Unsubscribe(jobId);
        }
    }

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
