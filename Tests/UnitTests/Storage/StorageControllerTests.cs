using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Shared.Messaging;
using Shared.Storage;
using TUnit.Core;
using WebAPI.Features.Storage.Controllers;
using WebAPI.Features.Storage.Models;

namespace UnitTests.Storage;

public class StorageControllerTests
{
    [Test]
    public async Task CreateLocalStorage_Sends_Request_And_Returns_Typed_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var logger = Substitute.For<ILogger<StorageController>>();
        var controller = new StorageController(bus, logger);
        var dto = StorageTestHelpers.CreateDto(StorageMethod.Local, "local-a");

        bus.RequestAsync<StorageCreateLocalRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateLocalStorage,
                Arg.Is<StorageCreateLocalRequestMessage>(x => x != null &&
                    x.Key == "local-a" &&
                    x.Description == "desc" &&
                    x.Parameters.Path == "/var/storage" &&
                    x.Parameters.Protocol == LocalStorageProtocol.Local),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = dto
            });

        var result = await controller.CreateLocalStorage(new LocalStorageUpsertRequest
        {
            Key = "local-a",
            Description = "desc",
            Protocol = LocalStorageProtocol.Local,
            Path = "/var/storage"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>()
            .Value!.ShouldBeOfType<LocalStorageConfigResponse>()
            .Path.ShouldBe("/mnt/storage");
    }

    [Test]
    public async Task UpdateNetworkStorage_Sends_Request_And_Returns_Typed_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());
        var dto = StorageTestHelpers.CreateDto(StorageMethod.Network, "network-a");

        bus.RequestAsync<StorageUpdateStreamingRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.UpdateNetworkStorage,
                Arg.Is<StorageUpdateStreamingRequestMessage>(x => x != null &&
                    x.Key == "network-a" &&
                    x.Parameters.Host == "host" &&
                    x.Parameters.Port == 22 &&
                    x.Parameters.Password == "pw"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = dto
            });

        var result = await controller.UpdateNetworkStorage("network-a", new NetworkStorageUpdateRequest
        {
            Description = "desc",
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "host",
            Port = 22,
            Username = "micah",
            Password = "pw"
        }, CancellationToken.None);

        var payload = result.Result!.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<NetworkStorageConfigResponse>();
        payload.Host.ShouldBe("example.test");
        payload.Port.ShouldBe(22);
        payload.Username.ShouldBe("micah");
    }

    [Test]
    public async Task CreateS3Storage_Sends_Request_And_Returns_Typed_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());
        var dto = StorageTestHelpers.CreateDto(StorageMethod.ObjectStorage, "s3-a");

        bus.RequestAsync<StorageCreateS3CompatibleObjectRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateS3CompatibleObjectStorage,
                Arg.Is<StorageCreateS3CompatibleObjectRequestMessage>(x => x != null &&
                    x.Key == "s3-a" &&
                    x.Parameters.Provider == S3CompatibleObjectStorageProvider.AwsS3 &&
                    x.Parameters.Region == "us-west-2" &&
                    x.Parameters.AccessKeyId == "access" &&
                    x.Parameters.SecretKeyId == "secret"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = dto
            });

        var result = await controller.CreateS3CompatibleObjectStorage(new S3CompatibleObjectStorageUpsertRequest
        {
            Key = "s3-a",
            Description = "desc",
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            Region = "us-west-2",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        }, CancellationToken.None);

        var payload = result.Result!.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<S3CompatibleObjectStorageConfigResponse>();
        payload.Provider.ShouldBe(S3CompatibleObjectStorageProvider.AwsS3);
        payload.HasSessionToken.ShouldBeTrue();
    }

    [Test]
    public async Task CreateAzureStorage_Sends_Request_And_Returns_Typed_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());
        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
        var dto = new StorageConfigDto
        {
            Id = 4,
            Key = "azure-a",
            Method = StorageMethod.ObjectStorage,
            Description = "desc",
            CreatedAt = now,
            LastUpdated = now,
            ObjectAzureBlob = new AzureBlobObjectStorageStored
            {
                CredentialMode = AzureBlobCredentialMode.AccountKey,
                ContainerName = "container",
                AzureAccountName = "account"
            }
        };

        bus.RequestAsync<StorageCreateAzureBlobObjectRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateAzureBlobObjectStorage,
                Arg.Is<StorageCreateAzureBlobObjectRequestMessage>(x => x != null &&
                    x.Key == "azure-a" &&
                    x.Parameters.CredentialMode == AzureBlobCredentialMode.AccountKey &&
                    x.Parameters.AzureAccountKeySecretId == "key"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = dto
            });

        var result = await controller.CreateAzureBlobObjectStorage(new AzureBlobObjectStorageUpsertRequest
        {
            Key = "azure-a",
            CredentialMode = AzureBlobCredentialMode.AccountKey,
            ContainerName = "container",
            AzureAccountName = "account",
            AzureAccountKeySecretId = "key"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>()
            .Value!.ShouldBeOfType<AzureBlobObjectStorageConfigResponse>()
            .AzureAccountName.ShouldBe("account");
    }

    [Test]
    public async Task CreateGoogleCloudStorage_Sends_Request_And_Returns_Typed_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());
        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
        var dto = new StorageConfigDto
        {
            Id = 5,
            Key = "gcs-a",
            Method = StorageMethod.ObjectStorage,
            Description = "desc",
            CreatedAt = now,
            LastUpdated = now,
            ObjectGoogleCloudStorage = new GoogleCloudStorageObjectStorageStored
            {
                BucketName = "bucket",
                CredentialMode = GoogleCloudStorageCredentialMode.CredentialsFilePath,
                GcpCredentialsFilePath = "/tmp/gcp.json",
                GcpProjectId = "proj"
            }
        };

        bus.RequestAsync<StorageCreateGoogleCloudStorageObjectRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateGoogleCloudStorageObjectStorage,
                Arg.Is<StorageCreateGoogleCloudStorageObjectRequestMessage>(x => x != null &&
                    x.Key == "gcs-a" &&
                    x.Parameters.BucketName == "bucket" &&
                    x.Parameters.GcpCredentialsFilePath == "/tmp/gcp.json"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = dto
            });

        var result = await controller.CreateGoogleCloudStorageObjectStorage(new GoogleCloudStorageObjectStorageUpsertRequest
        {
            Key = "gcs-a",
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsFilePath,
            GcpCredentialsFilePath = "/tmp/gcp.json",
            GcpProjectId = "proj"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>()
            .Value!.ShouldBeOfType<GoogleCloudStorageObjectStorageConfigResponse>()
            .GcpCredentialsFilePath.ShouldBe("/tmp/gcp.json");
    }

    [Test]
    public async Task Create_Returns_503_When_Bus_Returns_Null()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());

        bus.RequestAsync<StorageCreateLocalRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateLocalStorage,
                Arg.Any<StorageCreateLocalRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);

        var result = await controller.CreateLocalStorage(new LocalStorageUpsertRequest
        {
            Key = "local-a",
            Protocol = LocalStorageProtocol.Local,
            Path = "/tmp"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task Create_Returns_503_When_Bus_Throws_And_Logs()
    {
        var bus = Substitute.For<IMessageBus>();
        var logger = Substitute.For<ILogger<StorageController>>();
        var controller = new StorageController(bus, logger);

        bus.RequestAsync<StorageCreateLocalRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateLocalStorage,
                Arg.Any<StorageCreateLocalRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StorageOperationResponseMessage?>(new InvalidOperationException("boom")));

        var result = await controller.CreateLocalStorage(new LocalStorageUpsertRequest
        {
            Key = "local-a",
            Protocol = LocalStorageProtocol.Local,
            Path = "/tmp"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        logger.ReceivedCalls().Any().ShouldBeTrue();
    }

    [Test]
    public async Task Create_Maps_Error_Codes_To_Http_Statuses()
    {
        var cases = new Dictionary<string, int>
        {
            ["not_found"] = StatusCodes.Status404NotFound,
            ["conflict"] = StatusCodes.Status409Conflict,
            ["validation"] = StatusCodes.Status400BadRequest,
            ["forbidden"] = StatusCodes.Status403Forbidden,
            ["unknown"] = StatusCodes.Status500InternalServerError
        };

        foreach (var testCase in cases)
        {
            var bus = Substitute.For<IMessageBus>();
            var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());

            bus.RequestAsync<StorageCreateLocalRequestMessage, StorageOperationResponseMessage>(
                    StorageSubjects.CreateLocalStorage,
                    Arg.Any<StorageCreateLocalRequestMessage>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<CancellationToken>())
                .Returns(new StorageOperationResponseMessage
                {
                    Success = false,
                    ErrorCode = testCase.Key,
                    ErrorMessage = "failed"
                });

            var result = await controller.CreateLocalStorage(new LocalStorageUpsertRequest
            {
                Key = $"local-{testCase.Key}",
                Protocol = LocalStorageProtocol.Local,
                Path = "/tmp"
            }, CancellationToken.None);

            var actionResult = result.Result!.ShouldNotBeNull();
            (actionResult as ObjectResult)?.StatusCode.ShouldBe(testCase.Value);
        }
    }

    [Test]
    public async Task Create_Returns_502_On_Method_Mismatch()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());

        bus.RequestAsync<StorageCreateS3CompatibleObjectRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateS3CompatibleObjectStorage,
                Arg.Any<StorageCreateS3CompatibleObjectRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = StorageTestHelpers.CreateDto(StorageMethod.Local)
            });

        var result = await controller.CreateS3CompatibleObjectStorage(new S3CompatibleObjectStorageUpsertRequest
        {
            Key = "s3-a",
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            Region = "us-west-2",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task Delete_Maps_Success_And_NotFound()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());

        bus.RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.DeleteStorage,
                Arg.Is<StorageDeleteRequestMessage>(x => x != null && x.Key == "present"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        bus.RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.DeleteStorage,
                Arg.Is<StorageDeleteRequestMessage>(x => x != null && x.Key == "missing"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        (await controller.DeleteStorage("present", CancellationToken.None))
            .ShouldBeOfType<NoContentResult>();
        (await controller.DeleteStorage("missing", CancellationToken.None))
            .ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task List_Returns_Empty_And_Populated_Collections()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());

        bus.RequestAsync<StorageListRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.ListStorage,
                Arg.Any<StorageListRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new StorageOperationResponseMessage
                {
                    Success = true,
                    Items = []
                },
                new StorageOperationResponseMessage
                {
                    Success = true,
                    Items = [StorageTestHelpers.CreateDto(StorageMethod.Local, "local-a")]
                });

        var empty = await controller.ListStorage(CancellationToken.None);
        var populated = await controller.ListStorage(CancellationToken.None);
        var emptyItems = (IReadOnlyCollection<StorageConfigDto>)empty.Result!.ShouldBeOfType<OkObjectResult>().Value!;
        var populatedItems = (IReadOnlyCollection<StorageConfigDto>)populated.Result!.ShouldBeOfType<OkObjectResult>().Value!;

        emptyItems.Count.ShouldBe(0);
        populatedItems.Count.ShouldBe(1);
    }

    [Test]
    public async Task GetStorage_Returns_Entity_And_Rejects_Invalid_Service_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());
        var dto = StorageTestHelpers.CreateDto(StorageMethod.Local, "local-a");

        bus.RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.GetStorage,
                Arg.Is<StorageGetRequestMessage>(x => x != null && x.Key == "local-a"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Entity = dto });
        bus.RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.GetStorage,
                Arg.Is<StorageGetRequestMessage>(x => x != null && x.Key == "broken"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Entity = null });

        var ok = await controller.GetStorage("local-a", CancellationToken.None);
        ok.Result!.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<StorageConfigDto>().Key.ShouldBe("local-a");

        var broken = await controller.GetStorage("broken", CancellationToken.None);
        broken.Result!.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task UpdateLocalStorage_Sends_Request_And_Returns_Typed_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new StorageController(bus, Substitute.For<ILogger<StorageController>>());
        var dto = StorageTestHelpers.CreateDto(StorageMethod.Local, "local-a");

        bus.RequestAsync<StorageUpdateLocalRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.UpdateLocalStorage,
                Arg.Is<StorageUpdateLocalRequestMessage>(x => x != null &&
                    x.Key == "local-a" &&
                    x.Description == "updated" &&
                    x.Parameters.Protocol == LocalStorageProtocol.Local &&
                    x.Parameters.Path == "/mnt/updated"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Entity = dto });

        var result = await controller.UpdateLocalStorage("local-a", new LocalStorageUpdateRequest
        {
            Description = "updated",
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/updated"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<LocalStorageConfigResponse>().Key.ShouldBe("local-a");
    }
}
