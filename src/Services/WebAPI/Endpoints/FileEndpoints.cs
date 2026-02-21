using FlySwattr.NATS.Abstractions;
using Shared;
using Shared.Messages;

namespace WebAPI.Endpoints;

/// <summary>
/// File processing API endpoints.
/// </summary>
public static class FileEndpoints
{
    /// <summary>
    /// Maps file processing endpoints to the application.
    /// </summary>
    public static void MapFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/videos")
            .WithTags("Videos");

        group.MapPost("/download", async (
            string url,
            string storageKey,
            IJetStreamPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var jobId = Guid.NewGuid();
            var request = new FileDownloadRequest(jobId, url, storageKey);
            var messageId = $"download-{jobId}";
            await publisher.PublishAsync(Subjects.DownloadFile, request, messageId, cancellationToken: cancellationToken);
            return Results.Accepted($"/api/videos/{jobId}", new { message = "Download Video request queued", jobId, url, storageKey });
        })
        .WithName("DownloadVideo")
        .WithSummary("Queue a video for downloading by a worker");

        group.MapGet("/{jobId:guid}", async (
            Guid jobId,
            IMessageBus messageBus,
            CancellationToken cancellationToken) =>
        {
            var request = new JobStatusRequest(jobId);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var response = await messageBus.RequestAsync<JobStatusRequest, JobStatusResponse>(
                    Subjects.JobStatus,
                    request,
                    TimeSpan.FromSeconds(5),
                    cancellationToken: cts.Token);

                if (response is { Status: "NotFound" })
                    return Results.NotFound(new { jobId });

                return Results.Ok(response);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(504); // Gateway timeout
            }
        })
        .WithName("GetVideoJobStatus")
        .WithSummary("Gets the status of a video download job");
    }
}
