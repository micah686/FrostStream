using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Shared;
using Shared.Messaging;
using Shared.Storage;

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

    [HttpPost("local/create")]
    public async Task<ActionResult<LocalStorageConfigResponse>> CreateLocalStorage(
        [FromBody] LocalStorageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.CreateLocalStorage,
            new StorageCreateLocalRequestMessage
            {
                Key = request.Key,
                Description = request.Description,
                Parameters = new PosixLocalStorageParameters
                {
                    Protocol = request.Protocol,
                    Path = request.Path
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapLocalResponse, StorageMethod.Local);
    }

    [HttpPut("local/update/{key}")]
    public async Task<ActionResult<LocalStorageConfigResponse>> UpdateLocalStorage(
        string key,
        [FromBody] LocalStorageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.UpdateLocalStorage,
            new StorageUpdateLocalRequestMessage
            {
                Key = key,
                Description = request.Description,
                Parameters = new PosixLocalStorageParameters
                {
                    Protocol = request.Protocol,
                    Path = request.Path
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapLocalResponse, StorageMethod.Local);
    }

    [HttpPost("network/create")]
    public async Task<ActionResult<NetworkStorageConfigResponse>> CreateNetworkStorage(
        [FromBody] NetworkStorageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.CreateNetworkStorage,
            new StorageCreateStreamingRequestMessage
            {
                Key = request.Key,
                Description = request.Description,
                Parameters = new StreamingNetworkStorageParameters
                {
                    Protocol = request.Protocol,
                    Host = request.Host,
                    Port = request.Port,
                    Username = request.Username,
                    Password = request.Password,
                    PrivateKey = request.PrivateKey,
                    PublicKey = request.PublicKey,
                    BasePath = request.BasePath
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapNetworkResponse, StorageMethod.Network);
    }

    [HttpPut("network/update/{key}")]
    public async Task<ActionResult<NetworkStorageConfigResponse>> UpdateNetworkStorage(
        string key,
        [FromBody] NetworkStorageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.UpdateNetworkStorage,
            new StorageUpdateStreamingRequestMessage
            {
                Key = key,
                Description = request.Description,
                Parameters = new StreamingNetworkStorageParameters
                {
                    Protocol = request.Protocol,
                    Host = request.Host,
                    Port = request.Port,
                    Username = request.Username,
                    Password = request.Password,
                    PrivateKey = request.PrivateKey,
                    PublicKey = request.PublicKey,
                    BasePath = request.BasePath
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapNetworkResponse, StorageMethod.Network);
    }

    [HttpPost("object/create")]
    public async Task<ActionResult<ObjectStorageConfigResponse>> CreateObjectStorage(
        [FromBody] ObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.CreateObjectStorage,
            new StorageCreateObjectRequestMessage
            {
                Key = request.Key,
                Description = request.Description,
                Parameters = new ObjectStorageParameters
                {
                    Provider = request.Provider,
                    Container = request.Container,
                    Region = request.Region,
                    Endpoint = request.Endpoint,
                    BasePath = request.BasePath,
                    AccessKeyId = request.AccessKeyId,
                    SecretKey = request.SecretKey,
                    UseDefaultCredentials = request.UseDefaultCredentials
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapObjectResponse, StorageMethod.ObjectStorage);
    }

    [HttpPut("object/update/{key}")]
    public async Task<ActionResult<ObjectStorageConfigResponse>> UpdateObjectStorage(
        string key,
        [FromBody] ObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.UpdateObjectStorage,
            new StorageUpdateObjectRequestMessage
            {
                Key = key,
                Description = request.Description,
                Parameters = new ObjectStorageParameters
                {
                    Provider = request.Provider,
                    Container = request.Container,
                    Region = request.Region,
                    Endpoint = request.Endpoint,
                    BasePath = request.BasePath,
                    AccessKeyId = request.AccessKeyId,
                    SecretKey = request.SecretKey,
                    UseDefaultCredentials = request.UseDefaultCredentials
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapObjectResponse, StorageMethod.ObjectStorage);
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

    private ActionResult<TResponse> MapTypedStorageResponse<TResponse>(
        StorageOperationResponseMessage? response,
        Func<StorageConfigDto, TResponse> map,
        StorageMethod expectedMethod)
    {
        if (response is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to process storage request.");
        }

        if (!response.Success)
        {
            return MapErrorResponse(response);
        }

        if (response.Entity is null || response.Entity.Method != expectedMethod)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Storage service returned an invalid response.");
        }

        return Ok(map(response.Entity));
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

    private static LocalStorageConfigResponse MapLocalResponse(StorageConfigDto storage)
    {
        var local = storage.Local
            ?? throw new InvalidOperationException("Storage response does not contain local parameters.");

        return new LocalStorageConfigResponse
        {
            Id = storage.Id,
            Key = storage.Key,
            Description = storage.Description,
            CreatedAt = storage.CreatedAt,
            LastUpdated = storage.LastUpdated,
            Protocol = local.Protocol,
            Path = local.Path
        };
    }

    private static NetworkStorageConfigResponse MapNetworkResponse(StorageConfigDto storage)
    {
        var network = storage.Network
            ?? throw new InvalidOperationException("Storage response does not contain network parameters.");

        return new NetworkStorageConfigResponse
        {
            Id = storage.Id,
            Key = storage.Key,
            Description = storage.Description,
            CreatedAt = storage.CreatedAt,
            LastUpdated = storage.LastUpdated,
            Protocol = network.Protocol,
            Host = network.Host,
            Port = network.Port,
            Username = network.Username,
            Password = network.Password,
            PrivateKey = network.PrivateKey,
            PublicKey = network.PublicKey,
            BasePath = network.BasePath
        };
    }

    private static ObjectStorageConfigResponse MapObjectResponse(StorageConfigDto storage)
    {
        var @object = storage.Object
            ?? throw new InvalidOperationException("Storage response does not contain object parameters.");

        return new ObjectStorageConfigResponse
        {
            Id = storage.Id,
            Key = storage.Key,
            Description = storage.Description,
            CreatedAt = storage.CreatedAt,
            LastUpdated = storage.LastUpdated,
            Provider = @object.Provider,
            Container = @object.Container,
            Region = @object.Region,
            Endpoint = @object.Endpoint,
            BasePath = @object.BasePath,
            AccessKeyId = @object.AccessKeyId,
            SecretKey = @object.SecretKey,
            UseDefaultCredentials = @object.UseDefaultCredentials
        };
    }
}

public abstract class StorageUpsertRequestBase
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}

public abstract class StorageUpdateRequestBase
{
    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class LocalStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
{
    [Required]
    public LocalStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Path { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return StorageParametersSerializer.Validate(new PosixLocalStorageParameters
        {
            Protocol = Protocol,
            Path = Path
        })
            .Select(error => new ValidationResult(error, [nameof(Path)]));
    }
}

public sealed class LocalStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
{
    [Required]
    public LocalStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Path { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return StorageParametersSerializer.Validate(new PosixLocalStorageParameters
        {
            Protocol = Protocol,
            Path = Path
        })
            .Select(error => new ValidationResult(error, [nameof(Path)]));
    }
}

public sealed class NetworkStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
{
    [Required]
    public NetworkStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Host { get; init; }

    [Range(1, 65535)]
    public int? Port { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? PublicKey { get; init; }
    public string? BasePath { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return StorageParametersSerializer.Validate(new StreamingNetworkStorageParameters
        {
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            BasePath = BasePath
        }).Select(error => new ValidationResult(error));
    }
}

public sealed class NetworkStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
{
    [Required]
    public NetworkStorageProtocol Protocol { get; init; }

    [Required]
    [MinLength(1)]
    public required string Host { get; init; }

    [Range(1, 65535)]
    public int? Port { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? PublicKey { get; init; }
    public string? BasePath { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return StorageParametersSerializer.Validate(new StreamingNetworkStorageParameters
        {
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            Username = Username,
            Password = Password,
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            BasePath = BasePath
        }).Select(error => new ValidationResult(error));
    }
}

public sealed class ObjectStorageUpsertRequest : StorageUpsertRequestBase, IValidatableObject
{
    [Required]
    public ObjectStorageProtocol Provider { get; init; }

    [Required]
    [MinLength(1)]
    public required string Container { get; init; }

    public string? Region { get; init; }
    public string? Endpoint { get; init; }
    public string? BasePath { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretKey { get; init; }
    public bool UseDefaultCredentials { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return StorageParametersSerializer.Validate(new ObjectStorageParameters
        {
            Provider = Provider,
            Container = Container,
            Region = Region,
            Endpoint = Endpoint,
            BasePath = BasePath,
            AccessKeyId = AccessKeyId,
            SecretKey = SecretKey,
            UseDefaultCredentials = UseDefaultCredentials
        }).Select(error => new ValidationResult(error));
    }
}

public sealed class ObjectStorageUpdateRequest : StorageUpdateRequestBase, IValidatableObject
{
    [Required]
    public ObjectStorageProtocol Provider { get; init; }

    [Required]
    [MinLength(1)]
    public required string Container { get; init; }

    public string? Region { get; init; }
    public string? Endpoint { get; init; }
    public string? BasePath { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretKey { get; init; }
    public bool UseDefaultCredentials { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        return StorageParametersSerializer.Validate(new ObjectStorageParameters
        {
            Provider = Provider,
            Container = Container,
            Region = Region,
            Endpoint = Endpoint,
            BasePath = BasePath,
            AccessKeyId = AccessKeyId,
            SecretKey = SecretKey,
            UseDefaultCredentials = UseDefaultCredentials
        }).Select(error => new ValidationResult(error));
    }
}

public abstract class StorageConfigResponseBase
{
    public int Id { get; init; }
    public required string Key { get; init; }
    public string? Description { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed class LocalStorageConfigResponse : StorageConfigResponseBase
{
    public LocalStorageProtocol Protocol { get; init; }
    public required string Path { get; init; }
}

public sealed class NetworkStorageConfigResponse : StorageConfigResponseBase
{
    public NetworkStorageProtocol Protocol { get; init; }
    public required string Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? PublicKey { get; init; }
    public string? BasePath { get; init; }
}

public sealed class ObjectStorageConfigResponse : StorageConfigResponseBase
{
    public ObjectStorageProtocol Provider { get; init; }
    public required string Container { get; init; }
    public string? Region { get; init; }
    public string? Endpoint { get; init; }
    public string? BasePath { get; init; }
    public string? AccessKeyId { get; init; }
    public string? SecretKey { get; init; }
    public bool UseDefaultCredentials { get; init; }
}
