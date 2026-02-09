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
            IMessageBus messageBus,
            CancellationToken cancellationToken) =>
        {
            var request = new FileProcessRequest(filename, storageKey);
            await messageBus.PublishAsync(Subjects.ProcessFile, request, cancellationToken);
            return Results.Accepted(value: new { message = "File processing request queued", filename, storageKey });
        })
        .WithName("ProcessFile")
        .WithSummary("Queue a file for processing by a worker");
    }
}
