using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared.Messaging;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController(
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger<DownloadsController> logger) : ControllerBase
{
    /// <summary>
    /// Submits a new download/archive job. Publishes <see cref="DownloadRequested"/> to JetStream;
    /// DataBridge consumes it from <c>FROSTSTREAM_DOWNLOAD</c> and starts a Cleipnir flow keyed
    /// by the returned <c>JobId</c>.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<DownloadRequestResponse>> Submit(
        [FromBody] DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var message = new DownloadRequested
        {
            JobId = jobId,
            CorrelationId = correlationId,
            CausationId = null,
            MessageId = messageId,
            OperationKey = $"job/{jobId:N}/requested",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = request.SourceUrl,
            RequestedBy = request.RequestedBy,
            StorageKey = request.StorageKey,
            Tags = request.Tags
        };

        try
        {
            await publisher.PublishAsync(
                DownloadSubjects.DownloadRequested,
                message,
                messageId: messageId.ToString("N"),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed publishing DownloadRequested for JobId {JobId}", jobId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Failed to submit download request",
                Detail = "Could not publish to the messaging bus.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        return Accepted(new DownloadRequestResponse(jobId, correlationId));
    }
}

public sealed class DownloadRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    public string? RequestedBy { get; init; }

    public string? StorageKey { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record DownloadRequestResponse(Guid JobId, Guid CorrelationId);
