using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Shared.Messaging;

namespace WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly IMessageBus _messageBus;
    private readonly ILogger<StorageController> _logger;

    public StorageController(IMessageBus messageBus, ILogger<StorageController> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateStorage([FromBody] CreateStorageRequest request, CancellationToken cancellationToken)
    {
        var parameterErrors = StorageParametersSerializer.Validate(request.Method, request.Parameters);
        if (parameterErrors.Count > 0)
        {
            foreach (var error in parameterErrors)
            {
                ModelState.AddModelError(nameof(request.Parameters), error);
            }

            return ValidationProblem(ModelState);
        }

        var response = await SendRequestAsync(
            StorageSubjects.CreateStorage,
            new StorageCreateRequestMessage
            {
                Key = request.Key,
                Method = request.Method,
                Parameters = request.Parameters,
                Description = request.Description
            },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process storage create request.");
        }

        if (!response.Success)
        {
            return MapErrorResponse(response);
        }

        return Ok();
    }

    [HttpPut("update/{key}")]
    public async Task<ActionResult<StorageConfigDto>> UpdateStorage(string key, [FromBody] UpdateStorageRequest request, CancellationToken cancellationToken)
    {
        var parameterErrors = StorageParametersSerializer.Validate(request.Method, request.Parameters);
        if (parameterErrors.Count > 0)
        {
            foreach (var error in parameterErrors)
            {
                ModelState.AddModelError(nameof(request.Parameters), error);
            }

            return ValidationProblem(ModelState);
        }

        var response = await SendRequestAsync(
            StorageSubjects.UpdateStorage,
            new StorageUpdateRequestMessage
            {
                Key = key,
                Method = request.Method,
                Parameters = request.Parameters,
                Description = request.Description
            },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process storage update request.");
        }

        if (!response.Success)
        {
            return MapErrorResponse(response);
        }

        if (response.Entity is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Storage service returned an invalid update response.");
        }

        return Ok(response.Entity);
    }

    [HttpGet("list")]
    public async Task<ActionResult<IReadOnlyCollection<StorageConfigDto>>> ListStorage(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.ListStorage,
            new StorageListRequestMessage(),
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process storage list request.");
        }

        if (!response.Success)
        {
            return MapErrorResponse(response);
        }

        return Ok(response.Items ?? Array.Empty<StorageConfigDto>());
    }

    [HttpDelete("delete/{key}")]
    public async Task<IActionResult> DeleteStorage(string key, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.DeleteStorage,
            new StorageDeleteRequestMessage { Key = key },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process storage delete request.");
        }

        if (!response.Success)
        {
            return MapErrorResponse(response);
        }

        return NoContent();
    }

    [HttpGet("{key}")]
    public async Task<ActionResult<StorageConfigDto>> GetStorage(string key, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.GetStorage,
            new StorageGetRequestMessage { Key = key },
            cancellationToken);

        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process storage get request.");
        }

        if (!response.Success)
        {
            return MapErrorResponse(response);
        }

        if (response.Entity is null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Storage service returned an invalid get response.");
        }

        return Ok(response.Entity);
    }

    private async Task<StorageOperationResponseMessage?> SendRequestAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _messageBus.RequestAsync<TRequest, StorageOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed processing storage request on subject '{Subject}'", subject);
            return null;
        }
    }

    private ActionResult MapErrorResponse(StorageOperationResponseMessage response)
    {
        return response.ErrorCode switch
        {
            "not_found" => NotFound(response.ErrorMessage),
            "conflict" => Conflict(response.ErrorMessage),
            "validation" => BadRequest(response.ErrorMessage),
            "forbidden" => StatusCode(StatusCodes.Status403Forbidden, response.ErrorMessage),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Storage request failed.")
        };
    }
}

public sealed class CreateStorageRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    public StorageMethod Method { get; init; }

    [Required]
    public required string Parameters { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class UpdateStorageRequest
{
    // [Required]
    // [StringLength(100, MinimumLength = 2)]
    // [RegularExpression("^[a-z0-9-]{2,100}$")]
    // public required string Key { get; init; }

    [Required]
    public StorageMethod Method { get; init; }

    [Required]
    public required string Parameters { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}
