using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Storage;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Internal byte-transfer surface for MediaProcessor. Metadata/state still flows over NATS, but
/// source and rendition bytes move over HTTP streams and WebAPI performs the FluentStorage I/O.
/// </summary>
[ApiController]
[Route("api/internal/media-storage")]
[Authorize(AuthenticationSchemes = MediaProcessorAuthenticationDefaults.Scheme)]
public sealed class MediaStorageTransferController(
    IBlobStorageProvider blobStorageProvider,
    ILogger<MediaStorageTransferController> logger) : ControllerBase
{
    [HttpGet("{storageKey}/{**storagePath}")]
    [EndpointSummary("Download a media processor source blob")]
    [EndpointDescription("Internal MediaProcessor endpoint that streams a blob from the named storage target without exposing storage credentials or OpenBao access to the processor.")]
    public async Task<IActionResult> Download(
        string storageKey,
        string storagePath,
        CancellationToken cancellationToken)
    {
        if (!IsValidRequest(storageKey, storagePath, out var validationError))
            return BadRequest(validationError);

        return await this.ServeBlobAsync(
            blobStorageProvider,
            logger,
            storageKey,
            storagePath,
            subject: "media processor source blob",
            contentType: "application/octet-stream",
            enableRangeProcessing: false,
            cancellationToken: cancellationToken);
    }

    [HttpPut("{storageKey}/{**storagePath}")]
    [DisableRequestSizeLimit]
    [EndpointSummary("Upload a media processor output blob")]
    [EndpointDescription("Internal MediaProcessor endpoint that writes a streamed rendition output into the named storage target through WebAPI's FluentStorage provider.")]
    public async Task<IActionResult> Upload(
        string storageKey,
        string storagePath,
        CancellationToken cancellationToken)
    {
        if (!IsValidRequest(storageKey, storagePath, out var validationError))
            return BadRequest(validationError);

        try
        {
            var storage = await blobStorageProvider.GetAsync(storageKey, cancellationToken);
            await storage.SetObject(storagePath, Request.Body, append: false, cancellationToken);
            return NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed writing media processor output to storage {StorageKey} at {StoragePath}.",
                storageKey,
                storagePath);
            return StatusCode(StatusCodes.Status502BadGateway, "Media processor output could not be written to storage.");
        }
    }

    private static bool IsValidRequest(string storageKey, string storagePath, out string? error)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            error = "Storage key is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(storagePath) ||
            storagePath.StartsWith("/", StringComparison.Ordinal) ||
            storagePath.Contains("..", StringComparison.Ordinal))
        {
            error = "Storage path is invalid.";
            return false;
        }

        error = null;
        return true;
    }
}
