using Conduit.NATS;
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
    IClock clock,
    ILogger<ImportsController> logger) : ControllerBase
{
    [HttpPost("local-media")]
    [Endpoint(EndpointIds.ImportsLocalMedia)]
    [EndpointSummary("Queue a local media import")]
    [EndpointDescription("Queues a background import of media that already sits in the worker's static 'incoming' folder. The worker reads its incoming/manifest.json to discover files, copies them into the chosen destination storage, and this call returns immediately with the batch and correlation identifiers. Optionally pins the import to a specific worker tag.")]
    public async Task<ActionResult<LocalMediaImportRequestResponse>> ImportLocalMedia(
        [FromBody] LocalMediaImportRequest request,
        CancellationToken cancellationToken)
    {
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

        try
        {
            var message = new LocalMediaImportRequested
            {
                JobId = batchId,
                CorrelationId = correlationId,
                CausationId = null,
                MessageId = messageId,
                OperationKey = $"local-import/{batchId:N}/requested",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                StorageKey = request.StorageKey.Trim(),
                WorkerTag = string.IsNullOrWhiteSpace(request.WorkerTag) ? null : request.WorkerTag.Trim(),
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
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to submit local media import",
                Detail = "Could not publish to the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Accepted(new LocalMediaImportRequestResponse(batchId, correlationId));
    }
}
