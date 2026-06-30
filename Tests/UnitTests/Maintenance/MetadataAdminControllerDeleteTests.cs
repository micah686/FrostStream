using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Metadata.Controllers;

namespace UnitTests.Maintenance;

public sealed class MetadataAdminControllerDeleteTests
{
    private static readonly Guid MediaGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public async Task DeleteMedia_Returns_Ok_With_Payload_On_Success()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MediaDeleteRequest, MediaDeleteResponse>(
                MediaDeleteSubjects.Delete,
                Arg.Is<MediaDeleteRequest>(x => x.MediaGuid == MediaGuid),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaDeleteResponse { Success = true, FilesDeleted = 3, MediaRemoved = true });

        var result = await controller.DeleteMedia(MediaGuid, CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<MediaDeleteResponse>();
        payload.FilesDeleted.ShouldBe(3);
        payload.MediaRemoved.ShouldBeTrue();
    }

    [Test]
    public async Task DeleteMedia_Maps_NotFound_And_Conflict()
    {
        var notFound = await DeleteMediaWith(new MediaDeleteResponse
        {
            Success = false,
            ErrorCode = "not_found",
            ErrorMessage = "missing"
        });
        notFound.ShouldBeOfType<NotFoundObjectResult>();

        var conflict = await DeleteMediaWith(new MediaDeleteResponse
        {
            Success = false,
            ErrorCode = "conflict",
            ErrorMessage = "active download"
        });
        conflict.ShouldBeOfType<ConflictObjectResult>();
    }

    [Test]
    public async Task DeleteMedia_Null_Response_Maps_To_ServiceUnavailable()
    {
        // The controller's SendRequestAsync swallows bus failures and returns null.
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<MediaDeleteRequest, MediaDeleteResponse>(
                MediaDeleteSubjects.Delete,
                Arg.Any<MediaDeleteRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MediaDeleteResponse?>(new InvalidOperationException("bus down")));
        var controller = CreateController(bus);

        var result = await controller.DeleteMedia(MediaGuid, CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task DeleteMediaForStorageKey_Returns_Ok_For_NonLast_Copy()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MediaDeleteForStorageKeyRequest, MediaDeleteResponse>(
                MediaDeleteSubjects.DeleteForStorageKey,
                Arg.Is<MediaDeleteForStorageKeyRequest>(x => x.MediaGuid == MediaGuid && x.StorageKey == "storage-a"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaDeleteResponse { Success = true, FilesDeleted = 1, MediaRemoved = false });

        var result = await controller.DeleteMediaForStorageKey(MediaGuid, "storage-a", CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<MediaDeleteResponse>();
        payload.MediaRemoved.ShouldBeFalse();
        payload.FilesDeleted.ShouldBe(1);
    }

    [Test]
    public async Task DeleteMediaForStorageKey_Maps_Validation_To_BadRequest()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MediaDeleteForStorageKeyRequest, MediaDeleteResponse>(
                MediaDeleteSubjects.DeleteForStorageKey,
                Arg.Any<MediaDeleteForStorageKeyRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaDeleteResponse
            {
                Success = false,
                ErrorCode = "validation",
                ErrorMessage = "Storage key is required."
            });

        var result = await controller.DeleteMediaForStorageKey(MediaGuid, "storage-a", CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    private static async Task<IActionResult> DeleteMediaWith(MediaDeleteResponse response)
    {
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<MediaDeleteRequest, MediaDeleteResponse>(
                MediaDeleteSubjects.Delete,
                Arg.Any<MediaDeleteRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        return await CreateController(bus).DeleteMedia(MediaGuid, CancellationToken.None);
    }

    private static MetadataAdminController CreateController(IMessageBus bus)
        => new(
            Substitute.For<IJetStreamPublisher>(),
            bus,
            SystemClock.Instance,
            Substitute.For<ILogger<MetadataAdminController>>());
}
