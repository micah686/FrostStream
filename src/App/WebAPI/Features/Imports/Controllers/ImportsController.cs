using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;
using WebAPI.Features.Imports.Models;

namespace WebAPI.Features.Imports.Controllers;

[ApiController]
[Route("api/imports")]
public sealed class ImportsController(
    IJetStreamPublisher publisher,
    Func<string, IObjectStore> objectStoreFactory,
    IClock clock,
    ILogger<ImportsController> logger) : ControllerBase
{
    [HttpPost("local-media")]
    [Endpoint(EndpointIds.ImportsLocalMedia)]
    [EndpointSummary("Queue a local media import")]
    [EndpointDescription("Accepts a JSON manifest upload plus worker-local source root and destination storage key, stores the manifest in the local-media import object-store bucket, publishes an authenticated background import request, and returns the batch and correlation identifiers without waiting for files to be copied.")]
    public async Task<ActionResult<LocalMediaImportRequestResponse>> ImportLocalMedia(
        [FromForm] LocalMediaImportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Manifest is null || request.Manifest.Length <= 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid manifest",
                Detail = "manifest is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(request.SourceRoot))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid sourceRoot",
                Detail = "sourceRoot is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid storageKey",
                Detail = "storageKey is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var batchId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var objectBucket = LocalImportTopology.ManifestObjectStoreBucket;
        var objectKey = $"local-media/{batchId:N}/manifest.json";
        var objectStore = objectStoreFactory(objectBucket);

        try
        {
            await using (var stream = request.Manifest.OpenReadStream())
            {
                await objectStore.PutAsync(objectKey, stream, cancellationToken);
            }

            var message = new LocalMediaImportRequested
            {
                JobId = batchId,
                CorrelationId = correlationId,
                CausationId = null,
                MessageId = messageId,
                OperationKey = $"local-import/{batchId:N}/requested",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                ManifestObjectBucket = objectBucket,
                ManifestObjectKey = objectKey,
                SourceRoot = request.SourceRoot.Trim(),
                StorageKey = request.StorageKey.Trim(),
                RequestedBy = AuthConstants.FindSubject(User),
                RequestedByContext = string.IsNullOrWhiteSpace(request.RequestedBy) ? null : request.RequestedBy.Trim()
            };

            await publisher.PublishAsync(
                LocalImportSubjects.LocalMediaImportRequested,
                message,
                messageId: messageId.ToString("N"),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed submitting local media import BatchId {BatchId}.", batchId);
            try
            {
                await objectStore.DeleteAsync(objectKey, cancellationToken);
            }
            catch (Exception deleteEx)
            {
                logger.LogWarning(deleteEx, "Failed deleting local media import manifest object {ObjectKey}.", objectKey);
            }

            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to submit local media import",
                Detail = "Could not store the manifest or publish to the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Accepted(new LocalMediaImportRequestResponse(batchId, correlationId));
    }
}
