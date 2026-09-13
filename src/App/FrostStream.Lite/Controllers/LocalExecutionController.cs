using System.Text.Json;
using System.Text.Json.Serialization;
using DataBridge.Lite;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Shared.Application;

namespace FrostStream.Lite.Controllers;

[ApiController]
[Route("api/jobs/local")]
public sealed class LocalExecutionController(
    ILocalExecutionStore store,
    LocalExecutionDispatcher dispatcher,
    ILocalProgressHub<LocalExecutionEvent, LocalExecutionSnapshot> progressHub) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [HttpGet]
    public Task<LocalExecutionSnapshot> List(CancellationToken cancellationToken)
        => store.LoadSnapshotAsync("all", cancellationToken);

    [HttpGet("{workId:guid}")]
    public async Task<IActionResult> Get(Guid workId, CancellationToken cancellationToken)
    {
        var snapshot = await store.LoadSnapshotAsync(workId.ToString(), cancellationToken);
        return snapshot.Items.SingleOrDefault() is { } item ? Ok(item) : NotFound();
    }

    [HttpPost("{workId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid workId, CancellationToken cancellationToken)
        => await dispatcher.RequestCancellationAsync(workId, cancellationToken) ? Accepted() : NotFound();

    [HttpGet("stream")]
    public Task StreamAll(CancellationToken cancellationToken)
        => Stream("all", cancellationToken);

    [HttpGet("{workId:guid}/stream")]
    public Task StreamOne(Guid workId, CancellationToken cancellationToken)
        => Stream(workId.ToString(), cancellationToken);

    private async Task Stream(string streamKey, CancellationToken cancellationToken)
    {
        await using var subscription = await progressHub.SubscribeAsync(streamKey, cancellationToken: cancellationToken);
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await WriteFrame("snapshot", subscription.Snapshot, cancellationToken);
        await foreach (var evt in subscription.Events.ReadAllAsync(cancellationToken))
            await WriteFrame("state", evt, cancellationToken);
    }

    private async Task WriteFrame<T>(string eventName, T value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(value, JsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
