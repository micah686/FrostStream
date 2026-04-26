using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;
using WebAPI.Controllers;

namespace UnitTests.Storage;

public class StorageControllerTests
{
    private readonly IMessageBus _messageBus;
    private readonly StorageController _controller;

    private const string ValidLocalJson = """{"path":"/data/media"}""";
    private const string ValidSftpJson = """{"protocol":2,"host":"sftp.example.com","username":"user","privateKey":"ssh-key"}""";
    private const string ValidS3Json = """{"provider":0,"container":"my-bucket","region":"us-east-1","useDefaultCredentials":true}""";

    private static readonly StorageConfigDto SampleDto = new()
    {
        Id = 1,
        Key = "test-storage",
        Method = StorageMethod.PosixLocal,
        Parameters = ValidLocalJson
    };

    private static readonly StorageOperationResponseMessage SuccessResponse = new()
    {
        Success = true,
        Entity = SampleDto
    };

    public StorageControllerTests()
    {
        _messageBus = Substitute.For<IMessageBus>();
        _controller = CreateController(_messageBus);
    }

    private static StorageController CreateController(IMessageBus messageBus)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<ApiBehaviorOptions>(_ => { });
        services.Configure<ProblemDetailsOptions>(_ => { });
        services.AddSingleton<ProblemDetailsFactory, DefaultProblemDetailsFactory>();

        var sp = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = sp };

        var controller = new StorageController(messageBus, NullLogger<StorageController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return controller;
    }

    // ── CreateStorage ──────────────────────────────────────────────────────────

    [Test]
    public async Task CreateStorage_ValidRequest_ReturnsOk()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                StorageSubjects.CreateStorage,
                Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        result.ShouldBeOfType<OkResult>();
    }

    [Test]
    public async Task CreateStorage_InvalidParametersJson_ReturnsBadRequest()
    {
        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = "not-json" },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(400);
    }

    [Test]
    public async Task CreateStorage_MissingRequiredParameterField_ReturnsBadRequest()
    {
        // PosixLocal requires path — empty object fails validation
        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = "{}" },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(400);
    }

    [Test]
    public async Task CreateStorage_NullResponseFromBus_Returns503()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(503);
    }

    [Test]
    public async Task CreateStorage_BusThrows_Returns503()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("NATS timeout"));

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(503);
    }

    [Test]
    public async Task CreateStorage_ConflictErrorCode_Returns409()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "conflict", ErrorMessage = "Key exists." });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Test]
    public async Task CreateStorage_ValidationErrorCode_Returns400()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "validation", ErrorMessage = "Bad data." });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task CreateStorage_ForbiddenErrorCode_Returns403()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "forbidden", ErrorMessage = "No access." });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(403);
    }

    [Test]
    public async Task CreateStorage_UnknownErrorCode_Returns500()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "internal", ErrorMessage = "Oops." });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(500);
    }

    [Test]
    public async Task CreateStorage_ValidSftpParameters_ReturnsOk()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "sftp-storage", Method = StorageMethod.StreamingNetwork, Parameters = ValidSftpJson },
            CancellationToken.None);

        result.ShouldBeOfType<OkResult>();
    }

    [Test]
    public async Task CreateStorage_InvalidSftpParameters_UsernameWithoutCredential_ReturnsBadRequest()
    {
        // username set but neither password nor privateKey — fails StreamingNetworkStorageParameters.Validate
        var noCredentialJson = """{"protocol":2,"host":"sftp.example.com","username":"user"}""";

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "sftp-storage", Method = StorageMethod.StreamingNetwork, Parameters = noCredentialJson },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(400);
    }

    [Test]
    public async Task CreateStorage_ValidS3Parameters_ReturnsOk()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "s3-storage", Method = StorageMethod.ObjectStorage, Parameters = ValidS3Json },
            CancellationToken.None);

        result.ShouldBeOfType<OkResult>();
    }

    [Test]
    public async Task CreateStorage_InvalidS3Parameters_MissingRegion_ReturnsBadRequest()
    {
        // AwsS3 requires region — omitting it fails ObjectStorageParameters.Validate
        var noRegionJson = """{"provider":0,"container":"my-bucket","useDefaultCredentials":true}""";

        var result = await _controller.CreateStorage(
            new CreateStorageRequest { Key = "s3-storage", Method = StorageMethod.ObjectStorage, Parameters = noRegionJson },
            CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(400);
    }

    // ── UpdateStorage ──────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateStorage_ValidRequest_ReturnsOkWithEntity()
    {
        _messageBus
            .RequestAsync<StorageUpdateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageUpdateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResponse);

        var result = await _controller.UpdateStorage(
            "test-storage",
            new UpdateStorageRequest { Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.Value.ShouldBe(SampleDto);
    }

    [Test]
    public async Task UpdateStorage_InvalidParametersJson_ReturnsBadRequest()
    {
        var result = await _controller.UpdateStorage(
            "test-storage",
            new UpdateStorageRequest { Method = StorageMethod.PosixLocal, Parameters = "not-json" },
            CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(400);
    }

    [Test]
    public async Task UpdateStorage_NullResponseFromBus_Returns503()
    {
        _messageBus
            .RequestAsync<StorageUpdateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageUpdateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);

        var result = await _controller.UpdateStorage(
            "test-storage",
            new UpdateStorageRequest { Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(503);
    }

    [Test]
    public async Task UpdateStorage_SuccessButNullEntity_Returns502()
    {
        _messageBus
            .RequestAsync<StorageUpdateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageUpdateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Entity = null });

        var result = await _controller.UpdateStorage(
            "test-storage",
            new UpdateStorageRequest { Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(502);
    }

    [Test]
    public async Task UpdateStorage_NotFoundErrorCode_Returns404()
    {
        _messageBus
            .RequestAsync<StorageUpdateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageUpdateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "not_found", ErrorMessage = "Not found." });

        var result = await _controller.UpdateStorage(
            "test-storage",
            new UpdateStorageRequest { Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── ListStorage ───────────────────────────────────────────────────────────

    [Test]
    public async Task ListStorage_ReturnsItems()
    {
        var items = new List<StorageConfigDto> { SampleDto };
        _messageBus
            .RequestAsync<StorageListRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageListRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Items = items });

        var result = await _controller.ListStorage(CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        var returned = okResult.Value.ShouldBeAssignableTo<IReadOnlyCollection<StorageConfigDto>>();
        returned!.Count.ShouldBe(1);
        returned.First().Key.ShouldBe("test-storage");
    }

    [Test]
    public async Task ListStorage_NullItems_ReturnsEmptyCollection()
    {
        _messageBus
            .RequestAsync<StorageListRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageListRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Items = null });

        var result = await _controller.ListStorage(CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        var returned = okResult.Value.ShouldBeAssignableTo<IReadOnlyCollection<StorageConfigDto>>();
        returned!.ShouldBeEmpty();
    }

    [Test]
    public async Task ListStorage_NullResponseFromBus_Returns503()
    {
        _messageBus
            .RequestAsync<StorageListRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageListRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);

        var result = await _controller.ListStorage(CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(503);
    }

    // ── DeleteStorage ─────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteStorage_ValidKey_ReturnsNoContent()
    {
        _messageBus
            .RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageDeleteRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        var result = await _controller.DeleteStorage("test-storage", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Test]
    public async Task DeleteStorage_NullResponseFromBus_Returns503()
    {
        _messageBus
            .RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageDeleteRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);

        var result = await _controller.DeleteStorage("test-storage", CancellationToken.None);

        var objectResult = result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(503);
    }

    [Test]
    public async Task DeleteStorage_NotFoundErrorCode_Returns404()
    {
        _messageBus
            .RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageDeleteRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "not_found", ErrorMessage = "Key not found." });

        var result = await _controller.DeleteStorage("test-storage", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── GetStorage ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetStorage_ValidKey_ReturnsEntity()
    {
        _messageBus
            .RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageGetRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResponse);

        var result = await _controller.GetStorage("test-storage", CancellationToken.None);

        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.Value.ShouldBe(SampleDto);
    }

    [Test]
    public async Task GetStorage_NullResponseFromBus_Returns503()
    {
        _messageBus
            .RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageGetRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((StorageOperationResponseMessage?)null);

        var result = await _controller.GetStorage("test-storage", CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(503);
    }

    [Test]
    public async Task GetStorage_SuccessButNullEntity_Returns502()
    {
        _messageBus
            .RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageGetRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true, Entity = null });

        var result = await _controller.GetStorage("test-storage", CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(502);
    }

    [Test]
    public async Task GetStorage_NotFoundErrorCode_Returns404()
    {
        _messageBus
            .RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageGetRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = false, ErrorCode = "not_found", ErrorMessage = "Key not found." });

        var result = await _controller.GetStorage("test-storage", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── NATS subject routing ──────────────────────────────────────────────────

    [Test]
    public async Task CreateStorage_SendsToCorrectSubject()
    {
        _messageBus
            .RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageCreateRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        await _controller.CreateStorage(
            new CreateStorageRequest { Key = "my-storage", Method = StorageMethod.PosixLocal, Parameters = ValidLocalJson },
            CancellationToken.None);

        await _messageBus.Received(1).RequestAsync<StorageCreateRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.CreateStorage,
            Arg.Is<StorageCreateRequestMessage>(m => m.Key == "my-storage" && m.Method == StorageMethod.PosixLocal),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetStorage_SendsCorrectKeyInMessage()
    {
        _messageBus
            .RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageGetRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResponse);

        await _controller.GetStorage("my-key", CancellationToken.None);

        await _messageBus.Received(1).RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.GetStorage,
            Arg.Is<StorageGetRequestMessage>(m => m.Key == "my-key"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteStorage_SendsCorrectKeyInMessage()
    {
        _messageBus
            .RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
                Arg.Any<string>(), Arg.Any<StorageDeleteRequestMessage>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage { Success = true });

        await _controller.DeleteStorage("target-key", CancellationToken.None);

        await _messageBus.Received(1).RequestAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.DeleteStorage,
            Arg.Is<StorageDeleteRequestMessage>(m => m.Key == "target-key"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }
}
