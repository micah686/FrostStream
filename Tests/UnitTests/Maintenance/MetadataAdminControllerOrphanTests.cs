using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Controllers;

namespace UnitTests.Maintenance;

public sealed class MetadataAdminControllerOrphanTests
{
    [Test]
    public async Task ListOrphans_Maps_Items_To_Ok_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        var item = new OrphanCleanupItemDto
        {
            Id = 10,
            Kind = "media_without_metadata",
            State = "moved",
            StorageKey = "storage-a",
            OriginalStoragePath = "media/video.mp4",
            OrphanStoragePath = "orphaned/20260501/10/video.mp4",
            DetectedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            DeleteAfter = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        bus.RequestAsync<OrphanCleanupListRequest, OrphanCleanupListResponse>(
                OrphanCleanupSubjects.AdminList,
                Arg.Is<OrphanCleanupListRequest>(x =>
                    x.Kind == "media_without_metadata" &&
                    x.State == "moved" &&
                    x.PageSize == 25 &&
                    x.Page == 2),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new OrphanCleanupListResponse { Success = true, Items = [item] });

        var result = await controller.ListOrphans("media_without_metadata", "moved", 25, 2);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldNotBeNull()
            .ShouldBeAssignableTo<IReadOnlyList<OrphanCleanupItemDto>>();
        payload.Single().Id.ShouldBe(10);
    }

    [Test]
    public async Task RestoreFile_Returns_Ok_On_Success()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<RestoreOrphanRequest, RestoreOrphanResponse>(
                OrphanCleanupSubjects.AdminRestoreFile,
                Arg.Is<RestoreOrphanRequest>(x => x.OrphanId == 10),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new RestoreOrphanResponse { Success = true });

        var result = await controller.RestoreFile(10, CancellationToken.None);

        result.ShouldBeOfType<OkResult>();
    }

    [Test]
    public async Task RestoreMetadata_Maps_NotFound_Conflict_And_Unavailable()
    {
        var notFound = await RestoreMetadataWith(new RestoreOrphanResponse
        {
            Success = false,
            ErrorCode = "not_found",
            ErrorMessage = "missing"
        });
        notFound.ShouldBeOfType<NotFoundObjectResult>();

        var conflict = await RestoreMetadataWith(new RestoreOrphanResponse
        {
            Success = false,
            ErrorCode = "conflict",
            ErrorMessage = "conflict"
        });
        conflict.ShouldBeOfType<ConflictObjectResult>();

        var unavailable = await RestoreMetadataWith(new RestoreOrphanResponse
        {
            Success = false,
            ErrorCode = "unavailable",
            ErrorMessage = "down"
        });
        unavailable.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IActionResult> RestoreMetadataWith(RestoreOrphanResponse response)
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<RestoreOrphanRequest, RestoreOrphanResponse>(
                OrphanCleanupSubjects.AdminRestoreMetadata,
                Arg.Any<RestoreOrphanRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        return await controller.RestoreMetadata(10, CancellationToken.None);
    }

    private static MetadataAdminController CreateController(IMessageBus bus)
        => new(
            Substitute.For<IJetStreamPublisher>(),
            bus,
            SystemClock.Instance,
            Substitute.For<ILogger<MetadataAdminController>>());
}
