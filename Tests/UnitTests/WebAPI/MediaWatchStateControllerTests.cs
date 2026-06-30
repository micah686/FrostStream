using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Media.Controllers;

namespace UnitTests.WebAPI;

public sealed class MediaWatchStateControllerTests
{
    private static readonly Guid MediaGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 4, 12, 0);

    [Test]
    public async Task Upsert_Forwards_Authenticated_User_State()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<WatchStateUpsertRequest, WatchStateResponse>(
                WatchStateSubjects.Upsert,
                Arg.Is<WatchStateUpsertRequest>(x =>
                    x.OwnerSubject == "reader-1" &&
                    x.MediaGuid == MediaGuid &&
                    x.PositionSeconds == 120 &&
                    x.DurationSeconds == 200 &&
                    x.Completed),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchStateResponse
            {
                Success = true,
                State = State(completed: true)
            });

        var result = await controller.Upsert(
            MediaGuid,
            new WatchStateUpdateRequest
            {
                PositionSeconds = 120,
                DurationSeconds = 200,
                Completed = true
            },
            CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<WatchStateDto>();
        payload.OwnerSubject.ShouldBe("reader-1");
        payload.Completed.ShouldBeTrue();
    }

    [Test]
    public async Task Get_Returns_NotFound_For_Missing_State()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<WatchStateGetRequest, WatchStateResponse>(
                WatchStateSubjects.Get,
                Arg.Any<WatchStateGetRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchStateResponse { Success = true, State = null });

        var result = await controller.Get(MediaGuid, CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task Upsert_Maps_Missing_Media_To_NotFound()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<WatchStateUpsertRequest, WatchStateResponse>(
                WatchStateSubjects.Upsert,
                Arg.Any<WatchStateUpsertRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchStateResponse
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Upsert(MediaGuid, new WatchStateUpdateRequest(), CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task MarkWatched_Forwards_Completed_State_For_Authenticated_User()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<WatchStateUpsertRequest, WatchStateResponse>(
                WatchStateSubjects.Upsert,
                Arg.Is<WatchStateUpsertRequest>(x =>
                    x.OwnerSubject == "reader-1" &&
                    x.MediaGuid == MediaGuid &&
                    x.PositionSeconds == null &&
                    x.DurationSeconds == null &&
                    x.Completed),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchStateResponse
            {
                Success = true,
                State = State(completed: true)
            });

        var result = await controller.MarkWatched(MediaGuid, CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<WatchStateDto>();
        payload.Completed.ShouldBeTrue();
    }

    [Test]
    public async Task MarkUnwatched_Forwards_Incomplete_State_For_Authenticated_User()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<WatchStateUpsertRequest, WatchStateResponse>(
                WatchStateSubjects.Upsert,
                Arg.Is<WatchStateUpsertRequest>(x =>
                    x.OwnerSubject == "reader-1" &&
                    x.MediaGuid == MediaGuid &&
                    x.PositionSeconds == null &&
                    x.DurationSeconds == null &&
                    !x.Completed),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new WatchStateResponse
            {
                Success = true,
                State = State(completed: false)
            });

        var result = await controller.MarkUnwatched(MediaGuid, CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<WatchStateDto>();
        payload.Completed.ShouldBeFalse();
        payload.WatchedAt.ShouldBeNull();
    }

    private static MediaWatchStateController CreateController(IMessageBus bus)
    {
        var controller = new MediaWatchStateController(
            bus,
            Substitute.For<ILogger<MediaWatchStateController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(AuthConstants.SubjectClaim, "reader-1")],
                    "test"))
            }
        };
        return controller;
    }

    private static WatchStateDto State(bool completed)
        => new()
        {
            OwnerSubject = "reader-1",
            MediaGuid = MediaGuid,
            PositionSeconds = 120,
            DurationSeconds = 200,
            Completed = completed,
            WatchedAt = completed ? Now : null,
            LastPlayedAt = Now,
            UpdatedAt = Now
        };
}
