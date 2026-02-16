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
        var group = app.MapGroup("/api/files")
            .WithTags("Files");

        group.MapPost("/process", async (
            string filename,
            string storageKey,
            IJetStreamPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var request = new FileDownloadRequest(filename, storageKey);
            var messageId = $"download-{filename}-{storageKey}";
            await publisher.PublishAsync(Subjects.DownloadFile, request, messageId, cancellationToken: cancellationToken);
            return Results.Accepted(value: new { message = "Download File request queued", filename, storageKey });
        })
        .WithName("ProcessFile")
        .WithSummary("Queue a file for processing by a worker");
    }
}
