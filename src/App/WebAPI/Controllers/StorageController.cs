using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
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

    [HttpPost("object/s3-compatible/create")]
    public async Task<ActionResult<S3CompatibleObjectStorageConfigResponse>> CreateS3CompatibleObjectStorage(
        [FromBody] S3CompatibleObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.CreateS3CompatibleObjectStorage,
            new StorageCreateS3CompatibleObjectRequestMessage
            {
                Key = request.Key,
                Description = request.Description,
                Parameters = new S3CompatibleObjectStorageParameters
                {
                    Provider = request.Provider,
                    BucketName = request.BucketName,
                    Region = request.Region,
                    Endpoint = request.Endpoint,
                    AccessKeyId = request.AccessKeyId,
                    SecretKeyId = request.SecretKeyId,
                    SessionTokenSecretId = request.SessionTokenSecretId,
                    ForcePathStyle = request.ForcePathStyle,
                    UseSsl = request.UseSsl
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapS3CompatibleObjectResponse, StorageMethod.ObjectStorage);
    }

    [HttpPut("object/s3-compatible/update/{key}")]
    public async Task<ActionResult<S3CompatibleObjectStorageConfigResponse>> UpdateS3CompatibleObjectStorage(
        string key,
        [FromBody] S3CompatibleObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.UpdateS3CompatibleObjectStorage,
            new StorageUpdateS3CompatibleObjectRequestMessage
            {
                Key = key,
                Description = request.Description,
                Parameters = new S3CompatibleObjectStorageParameters
                {
                    Provider = request.Provider,
                    BucketName = request.BucketName,
                    Region = request.Region,
                    Endpoint = request.Endpoint,
                    AccessKeyId = request.AccessKeyId,
                    SecretKeyId = request.SecretKeyId,
                    SessionTokenSecretId = request.SessionTokenSecretId,
                    ForcePathStyle = request.ForcePathStyle,
                    UseSsl = request.UseSsl
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapS3CompatibleObjectResponse, StorageMethod.ObjectStorage);
    }

    [HttpPost("object/azure-blob/create")]
    public async Task<ActionResult<AzureBlobObjectStorageConfigResponse>> CreateAzureBlobObjectStorage(
        [FromBody] AzureBlobObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.CreateAzureBlobObjectStorage,
            new StorageCreateAzureBlobObjectRequestMessage
            {
                Key = request.Key,
                Description = request.Description,
                Parameters = new AzureBlobObjectStorageParameters
                {
                    CredentialMode = request.CredentialMode,
                    ContainerName = request.ContainerName,
                    AzureAccountName = request.AzureAccountName,
                    AzureAccountKeySecretId = request.AzureAccountKeySecretId,
                    AzureConnectionStringSecretId = request.AzureConnectionStringSecretId,
                    AzureSasUrlSecretId = request.AzureSasUrlSecretId
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapAzureBlobObjectResponse, StorageMethod.ObjectStorage);
    }

    [HttpPut("object/azure-blob/update/{key}")]
    public async Task<ActionResult<AzureBlobObjectStorageConfigResponse>> UpdateAzureBlobObjectStorage(
        string key,
        [FromBody] AzureBlobObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.UpdateAzureBlobObjectStorage,
            new StorageUpdateAzureBlobObjectRequestMessage
            {
                Key = key,
                Description = request.Description,
                Parameters = new AzureBlobObjectStorageParameters
                {
                    CredentialMode = request.CredentialMode,
                    ContainerName = request.ContainerName,
                    AzureAccountName = request.AzureAccountName,
                    AzureAccountKeySecretId = request.AzureAccountKeySecretId,
                    AzureConnectionStringSecretId = request.AzureConnectionStringSecretId,
                    AzureSasUrlSecretId = request.AzureSasUrlSecretId
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapAzureBlobObjectResponse, StorageMethod.ObjectStorage);
    }

    [HttpPost("object/google-cloud-storage/create")]
    public async Task<ActionResult<GoogleCloudStorageObjectStorageConfigResponse>> CreateGoogleCloudStorageObjectStorage(
        [FromBody] GoogleCloudStorageObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.CreateGoogleCloudStorageObjectStorage,
            new StorageCreateGoogleCloudStorageObjectRequestMessage
            {
                Key = request.Key,
                Description = request.Description,
                Parameters = new GoogleCloudStorageObjectStorageParameters
                {
                    BucketName = request.BucketName,
                    CredentialMode = request.CredentialMode,
                    GcpCredentialsJson = request.GcpCredentialsJson,
                    GcpCredentialsJsonIsBase64Encoded = request.GcpCredentialsJsonIsBase64Encoded,
                    GcpCredentialsFilePath = request.GcpCredentialsFilePath,
                    GcpProjectId = request.GcpProjectId
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapGoogleCloudStorageObjectResponse, StorageMethod.ObjectStorage);
    }

    [HttpPut("object/google-cloud-storage/update/{key}")]
    public async Task<ActionResult<GoogleCloudStorageObjectStorageConfigResponse>> UpdateGoogleCloudStorageObjectStorage(
        string key,
        [FromBody] GoogleCloudStorageObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            StorageSubjects.UpdateGoogleCloudStorageObjectStorage,
            new StorageUpdateGoogleCloudStorageObjectRequestMessage
            {
                Key = key,
                Description = request.Description,
                Parameters = new GoogleCloudStorageObjectStorageParameters
                {
                    BucketName = request.BucketName,
                    CredentialMode = request.CredentialMode,
                    GcpCredentialsJson = request.GcpCredentialsJson,
                    GcpCredentialsJsonIsBase64Encoded = request.GcpCredentialsJsonIsBase64Encoded,
                    GcpCredentialsFilePath = request.GcpCredentialsFilePath,
                    GcpProjectId = request.GcpProjectId
                }
            },
            cancellationToken);

        return MapTypedStorageResponse(response, MapGoogleCloudStorageObjectResponse, StorageMethod.ObjectStorage);
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
            BasePath = network.BasePath
        };
    }

    private static S3CompatibleObjectStorageConfigResponse MapS3CompatibleObjectResponse(StorageConfigDto storage)
    {
        var @object = storage.ObjectS3Compatible
            ?? throw new InvalidOperationException("Storage response does not contain S3-compatible object parameters.");

        return new S3CompatibleObjectStorageConfigResponse
        {
            Id = storage.Id,
            Key = storage.Key,
            Description = storage.Description,
            CreatedAt = storage.CreatedAt,
            LastUpdated = storage.LastUpdated,
            Provider = @object.Provider,
            BucketName = @object.BucketName,
            Region = @object.Region,
            Endpoint = @object.Endpoint,
            HasSessionToken = @object.HasSessionToken,
            ForcePathStyle = @object.ForcePathStyle,
            UseSsl = @object.UseSsl
        };
    }

    private static AzureBlobObjectStorageConfigResponse MapAzureBlobObjectResponse(StorageConfigDto storage)
    {
        var @object = storage.ObjectAzureBlob
            ?? throw new InvalidOperationException("Storage response does not contain Azure Blob object parameters.");

        return new AzureBlobObjectStorageConfigResponse
        {
            Id = storage.Id,
            Key = storage.Key,
            Description = storage.Description,
            CreatedAt = storage.CreatedAt,
            LastUpdated = storage.LastUpdated,
            CredentialMode = @object.CredentialMode,
            ContainerName = @object.ContainerName,
            AzureAccountName = @object.AzureAccountName
        };
    }

    private static GoogleCloudStorageObjectStorageConfigResponse MapGoogleCloudStorageObjectResponse(StorageConfigDto storage)
    {
        var @object = storage.ObjectGoogleCloudStorage
            ?? throw new InvalidOperationException("Storage response does not contain Google Cloud Storage object parameters.");

        return new GoogleCloudStorageObjectStorageConfigResponse
        {
            Id = storage.Id,
            Key = storage.Key,
            Description = storage.Description,
            CreatedAt = storage.CreatedAt,
            LastUpdated = storage.LastUpdated,
            BucketName = @object.BucketName,
            CredentialMode = @object.CredentialMode,
            GcpCredentialsFilePath = @object.GcpCredentialsFilePath,
            GcpProjectId = @object.GcpProjectId
        };
    }
}
