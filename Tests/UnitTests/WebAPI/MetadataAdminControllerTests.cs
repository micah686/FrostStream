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

namespace UnitTests.WebAPI;

public sealed class MetadataAdminControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 18, 0);

    [Test]
    public async Task TriggerReindex_Publishes_Manual_Search_Reindex_Request()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        var result = await controller.TriggerReindex(CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        await publisher.Received(1).PublishAsync(
            BackgroundJobSubjects.SearchReindexRequest,
            Arg.Is<SearchReindexRequested>(x => x != null &&
                x.ScheduleKey == BackgroundJobRequestFactory.ManualScheduleKey &&
                x.TaskType == BackgroundJobRequestFactory.ManualSearchReindexTaskType &&
                x.DueWindowUtc == Now &&
                x.OccurredAt == Now &&
                x.IdempotencyKey == "manual_search_reindex:manual:2026-06-03T18:00:00Z"),
            "manual_search_reindex:manual:2026-06-03T18:00:00Z",
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriggerReindex_Returns_503_When_Publish_Fails()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        publisher.PublishAsync(
                Arg.Any<string>(),
                Arg.Any<SearchReindexRequested>(),
                Arg.Any<string>(),
                null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("nats unavailable")));

        var result = await controller.TriggerReindex(CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task ListOrphans_Maps_Validation_Unavailable_And_Default_Errors()
    {
        var validation = await ListOrphansWith(new OrphanCleanupListResponse
        {
            Success = false,
            ErrorCode = "validation",
            ErrorMessage = "bad"
        });
        validation.Result!.ShouldBeOfType<BadRequestObjectResult>().Value!.ShouldBe("bad");

        var unavailable = await ListOrphansWith(new OrphanCleanupListResponse
        {
            Success = false,
            ErrorCode = "unavailable",
            ErrorMessage = "down"
        });
        unavailable.Result!.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        var unknown = await ListOrphansWith(new OrphanCleanupListResponse
        {
            Success = false,
            ErrorCode = "unknown",
            ErrorMessage = "failed"
        });
        unknown.Result!.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public async Task UpdateWatchedAutoDeletePolicy_Forwards_Admin_Request()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);
        bus.RequestAsync<WatchedAutoDeletePolicyUpdateRequest, WatchedAutoDeletePolicyResponse>(
                WatchedAutoDeleteSubjects.UpdatePolicy,
                Arg.Is<WatchedAutoDeletePolicyUpdateRequest>(x => x != null &&
                    x.Enabled &&
                    x.DeleteAfterDays == 14 &&
                    x.MaxDeletionsPerRun == 25),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchedAutoDeletePolicyResponse
            {
                Success = true,
                Policy = new WatchedAutoDeletePolicyDto
                {
                    Enabled = true,
                    DeleteAfterDays = 14,
                    MaxDeletionsPerRun = 25
                }
            });

        var result = await controller.UpdateWatchedAutoDeletePolicy(
            new WatchedAutoDeletePolicyUpdateHttpRequest
            {
                Enabled = true,
                DeleteAfterDays = 14,
                MaxDeletionsPerRun = 25
            },
            CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<WatchedAutoDeletePolicyDto>();
        payload.Enabled.ShouldBeTrue();
        payload.DeleteAfterDays.ShouldBe(14);
        payload.MaxDeletionsPerRun.ShouldBe(25);
    }

    [Test]
    public async Task RunWatchedAutoDelete_Returns_Cleanup_Result()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);
        bus.RequestAsync<WatchedAutoDeleteCleanupRunRequest, WatchedAutoDeleteCleanupResponse>(
                WatchedAutoDeleteSubjects.RunCleanup,
                Arg.Any<WatchedAutoDeleteCleanupRunRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchedAutoDeleteCleanupResponse
            {
                Success = true,
                Result = new WatchedAutoDeleteCleanupResultDto
                {
                    PolicyEnabled = true,
                    Cutoff = Now,
                    CandidatesFound = 2,
                    DeletedCount = 1,
                    FailedCount = 1,
                    FilesDeleted = 3
                }
            });

        var result = await controller.RunWatchedAutoDelete(CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<WatchedAutoDeleteCleanupResultDto>();
        payload.DeletedCount.ShouldBe(1);
        payload.FailedCount.ShouldBe(1);
        payload.FilesDeleted.ShouldBe(3);
    }

    private static async Task<ActionResult<IReadOnlyList<OrphanCleanupItemDto>>> ListOrphansWith(
        OrphanCleanupListResponse response)
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);
        bus.RequestAsync<OrphanCleanupListRequest, OrphanCleanupListResponse>(
                OrphanCleanupSubjects.AdminList,
                Arg.Any<OrphanCleanupListRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        return await controller.ListOrphans(cancellationToken: CancellationToken.None);
    }

    private static MetadataAdminController CreateController(
        IJetStreamPublisher? publisher = null,
        IMessageBus? bus = null)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        return new MetadataAdminController(
            publisher ?? Substitute.For<IJetStreamPublisher>(),
            bus ?? Substitute.For<IMessageBus>(),
            clock,
            Substitute.For<ILogger<MetadataAdminController>>());
    }
}
