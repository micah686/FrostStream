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
        => await CreateStorageAsync<LocalStorageUpsertRequest, StorageCreateLocalRequestMessage, PosixLocalStorageParameters, LocalStorageConfigResponse>(
            StorageSubjects.CreateLocalStorage,
            request,
            (key, description, parameters) => new StorageCreateLocalRequestMessage { Key = key, Description = description, Parameters = parameters },
            MapLocalResponse,
            StorageMethod.Local,
            cancellationToken);

    [HttpPut("local/update/{key}")]
    public async Task<ActionResult<LocalStorageConfigResponse>> UpdateLocalStorage(
        string key,
        [FromBody] LocalStorageUpdateRequest request,
        CancellationToken cancellationToken)
        => await UpdateStorageAsync<LocalStorageUpdateRequest, StorageUpdateLocalRequestMessage, PosixLocalStorageParameters, LocalStorageConfigResponse>(
            StorageSubjects.UpdateLocalStorage,
            key,
            request,
            (storageKey, description, parameters) => new StorageUpdateLocalRequestMessage { Key = storageKey, Description = description, Parameters = parameters },
            MapLocalResponse,
            StorageMethod.Local,
            cancellationToken);

    [HttpPost("network/create")]
    public async Task<ActionResult<NetworkStorageConfigResponse>> CreateNetworkStorage(
        [FromBody] NetworkStorageUpsertRequest request,
        CancellationToken cancellationToken)
        => await CreateStorageAsync<NetworkStorageUpsertRequest, StorageCreateStreamingRequestMessage, StreamingNetworkStorageParameters, NetworkStorageConfigResponse>(
            StorageSubjects.CreateNetworkStorage,
            request,
            (key, description, parameters) => new StorageCreateStreamingRequestMessage { Key = key, Description = description, Parameters = parameters },
            MapNetworkResponse,
            StorageMethod.Network,
            cancellationToken);

    [HttpPut("network/update/{key}")]
    public async Task<ActionResult<NetworkStorageConfigResponse>> UpdateNetworkStorage(
        string key,
        [FromBody] NetworkStorageUpdateRequest request,
        CancellationToken cancellationToken)
        => await UpdateStorageAsync<NetworkStorageUpdateRequest, StorageUpdateStreamingRequestMessage, StreamingNetworkStorageParameters, NetworkStorageConfigResponse>(
            StorageSubjects.UpdateNetworkStorage,
            key,
            request,
            (storageKey, description, parameters) => new StorageUpdateStreamingRequestMessage { Key = storageKey, Description = description, Parameters = parameters },
            MapNetworkResponse,
            StorageMethod.Network,
            cancellationToken);

    [HttpPost("object/s3-compatible/create")]
    public async Task<ActionResult<S3CompatibleObjectStorageConfigResponse>> CreateS3CompatibleObjectStorage(
        [FromBody] S3CompatibleObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
        => await CreateStorageAsync<S3CompatibleObjectStorageUpsertRequest, StorageCreateS3CompatibleObjectRequestMessage, S3CompatibleObjectStorageParameters, S3CompatibleObjectStorageConfigResponse>(
            StorageSubjects.CreateS3CompatibleObjectStorage,
            request,
            (key, description, parameters) => new StorageCreateS3CompatibleObjectRequestMessage { Key = key, Description = description, Parameters = parameters },
            MapS3CompatibleObjectResponse,
            StorageMethod.ObjectStorage,
            cancellationToken);

    [HttpPut("object/s3-compatible/update/{key}")]
    public async Task<ActionResult<S3CompatibleObjectStorageConfigResponse>> UpdateS3CompatibleObjectStorage(
        string key,
        [FromBody] S3CompatibleObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
        => await UpdateStorageAsync<S3CompatibleObjectStorageUpdateRequest, StorageUpdateS3CompatibleObjectRequestMessage, S3CompatibleObjectStorageParameters, S3CompatibleObjectStorageConfigResponse>(
            StorageSubjects.UpdateS3CompatibleObjectStorage,
            key,
            request,
            (storageKey, description, parameters) => new StorageUpdateS3CompatibleObjectRequestMessage { Key = storageKey, Description = description, Parameters = parameters },
            MapS3CompatibleObjectResponse,
            StorageMethod.ObjectStorage,
            cancellationToken);

    [HttpPost("object/azure-blob/create")]
    public async Task<ActionResult<AzureBlobObjectStorageConfigResponse>> CreateAzureBlobObjectStorage(
        [FromBody] AzureBlobObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
        => await CreateStorageAsync<AzureBlobObjectStorageUpsertRequest, StorageCreateAzureBlobObjectRequestMessage, AzureBlobObjectStorageParameters, AzureBlobObjectStorageConfigResponse>(
            StorageSubjects.CreateAzureBlobObjectStorage,
            request,
            (key, description, parameters) => new StorageCreateAzureBlobObjectRequestMessage { Key = key, Description = description, Parameters = parameters },
            MapAzureBlobObjectResponse,
            StorageMethod.ObjectStorage,
            cancellationToken);

    [HttpPut("object/azure-blob/update/{key}")]
    public async Task<ActionResult<AzureBlobObjectStorageConfigResponse>> UpdateAzureBlobObjectStorage(
        string key,
        [FromBody] AzureBlobObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
        => await UpdateStorageAsync<AzureBlobObjectStorageUpdateRequest, StorageUpdateAzureBlobObjectRequestMessage, AzureBlobObjectStorageParameters, AzureBlobObjectStorageConfigResponse>(
            StorageSubjects.UpdateAzureBlobObjectStorage,
            key,
            request,
            (storageKey, description, parameters) => new StorageUpdateAzureBlobObjectRequestMessage { Key = storageKey, Description = description, Parameters = parameters },
            MapAzureBlobObjectResponse,
            StorageMethod.ObjectStorage,
            cancellationToken);

    [HttpPost("object/google-cloud-storage/create")]
    public async Task<ActionResult<GoogleCloudStorageObjectStorageConfigResponse>> CreateGoogleCloudStorageObjectStorage(
        [FromBody] GoogleCloudStorageObjectStorageUpsertRequest request,
        CancellationToken cancellationToken)
        => await CreateStorageAsync<GoogleCloudStorageObjectStorageUpsertRequest, StorageCreateGoogleCloudStorageObjectRequestMessage, GoogleCloudStorageObjectStorageParameters, GoogleCloudStorageObjectStorageConfigResponse>(
            StorageSubjects.CreateGoogleCloudStorageObjectStorage,
            request,
            (key, description, parameters) => new StorageCreateGoogleCloudStorageObjectRequestMessage { Key = key, Description = description, Parameters = parameters },
            MapGoogleCloudStorageObjectResponse,
            StorageMethod.ObjectStorage,
            cancellationToken);

    [HttpPut("object/google-cloud-storage/update/{key}")]
    public async Task<ActionResult<GoogleCloudStorageObjectStorageConfigResponse>> UpdateGoogleCloudStorageObjectStorage(
        string key,
        [FromBody] GoogleCloudStorageObjectStorageUpdateRequest request,
        CancellationToken cancellationToken)
        => await UpdateStorageAsync<GoogleCloudStorageObjectStorageUpdateRequest, StorageUpdateGoogleCloudStorageObjectRequestMessage, GoogleCloudStorageObjectStorageParameters, GoogleCloudStorageObjectStorageConfigResponse>(
            StorageSubjects.UpdateGoogleCloudStorageObjectStorage,
            key,
            request,
            (storageKey, description, parameters) => new StorageUpdateGoogleCloudStorageObjectRequestMessage { Key = storageKey, Description = description, Parameters = parameters },
            MapGoogleCloudStorageObjectResponse,
            StorageMethod.ObjectStorage,
            cancellationToken);

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

    private async Task<ActionResult<TResponse>> CreateStorageAsync<TRequest, TMessage, TParameters, TResponse>(
        string subject,
        TRequest request,
        Func<string, string?, TParameters, TMessage> messageFactory,
        Func<StorageConfigDto, TResponse> map,
        StorageMethod expectedMethod,
        CancellationToken cancellationToken)
        where TRequest : IStorageUpsertRequest<TParameters>
        where TParameters : StorageParametersBase
    {
        var response = await SendRequestAsync(
            subject,
            messageFactory(request.Key, request.Description, request.ToParameters()),
            cancellationToken);

        return MapTypedStorageResponse(response, map, expectedMethod);
    }

    private async Task<ActionResult<TResponse>> UpdateStorageAsync<TRequest, TMessage, TParameters, TResponse>(
        string subject,
        string key,
        TRequest request,
        Func<string, string?, TParameters, TMessage> messageFactory,
        Func<StorageConfigDto, TResponse> map,
        StorageMethod expectedMethod,
        CancellationToken cancellationToken)
        where TRequest : IStorageRequest<TParameters>
        where TParameters : StorageParametersBase
    {
        var response = await SendRequestAsync(
            subject,
            messageFactory(key, request.Description, request.ToParameters()),
            cancellationToken);

        return MapTypedStorageResponse(response, map, expectedMethod);
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
