using FlySwattr.NATS.Abstractions;
using Shared;
using Shared.Messages;

namespace WebAPI.Endpoints;

public sealed record DownloadVideoRequest(string Url, string StorageKey);

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

        group.MapPost("/download", QueueDownloadAsync)
        .WithName("DownloadVideo")
        .WithSummary("Queue a video for downloading by a worker")
        .Accepts<DownloadVideoRequest>("application/json")
        .Produces(StatusCodes.Status202Accepted)
        .ProducesValidationProblem();

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

    private static async Task<IResult> QueueDownloadAsync(
        DownloadVideoRequest request,
        IJetStreamPublisher publisher,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateDownloadRequest(request);
        if (validationErrors != null)
        {
            return TypedResults.ValidationProblem(validationErrors);
        }

        var jobId = Guid.NewGuid();
        var downloadRequest = new FileDownloadRequest(jobId, request.Url, request.StorageKey);
        var messageId = $"download-{jobId}";
        await publisher.PublishAsync(Subjects.DownloadFile, downloadRequest, messageId, cancellationToken: cancellationToken);

        return TypedResults.Accepted($"/api/videos/{jobId}", new
        {
            message = "Download video request queued",
            jobId,
            request.Url,
            request.StorageKey
        });
    }

    private static Dictionary<string, string[]>? ValidateDownloadRequest(DownloadVideoRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Url)
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors["url"] = ["Provide an absolute http/https URL."];
        }

        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            errors["storageKey"] = ["Storage key is required."];
        }
        else if (request.StorageKey.Length > 100)
        {
            errors["storageKey"] = ["Storage key must be 100 characters or fewer."];
        }

        return errors.Count == 0 ? null : errors;
    }
}
