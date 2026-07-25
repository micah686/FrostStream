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

public sealed class MediaLikesControllerTests
{
    private static readonly Guid MediaGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 4, 12, 0);

    [Test]
    public async Task Like_Forwards_Authenticated_User_State()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MediaLikeStateRequest, MediaLikeStateResponse>(
                MediaLikeSubjects.Like,
                Arg.Is<MediaLikeStateRequest>(x => x != null && x.OwnerSubject == "reader-1" && x.MediaGuid == MediaGuid),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaLikeStateResponse
            {
                Success = true,
                State = Liked()
            });

        var result = await controller.Like(MediaGuid, CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<MediaLikeStateDto>();
        payload.Liked.ShouldBeTrue();
        payload.OwnerSubject.ShouldBe("reader-1");
    }

    [Test]
    public async Task Unlike_Maps_Missing_Media_To_NotFound()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MediaLikeStateRequest, MediaLikeStateResponse>(
                MediaLikeSubjects.Unlike,
                Arg.Any<MediaLikeStateRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaLikeStateResponse
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Unlike(MediaGuid, CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task List_Forwards_Authenticated_User_Request()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MediaLikeListRequest, MediaLikeListResponse>(
                MediaLikeSubjects.List,
                Arg.Is<MediaLikeListRequest>(x => x != null &&
                    x.OwnerSubject == "reader-1" &&
                    x.Page == 2 &&
                    x.PageSize == 10),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaLikeListResponse
            {
                Success = true,
                Page = 2,
                TotalCount = 0,
                HasMore = false
            });

        var result = await controller.List(10, 2, CancellationToken.None);

        var payload = result.ShouldBeOfType<OkObjectResult>().Value!.ShouldBeOfType<MediaLikeListResponse>();
        payload.Page.ShouldBe(2);
    }

    private static MediaLikesController CreateController(IMessageBus bus)
    {
        var controller = new MediaLikesController(
            bus,
            Substitute.For<ILogger<MediaLikesController>>());
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

    private static MediaLikeStateDto Liked()
        => new()
        {
            OwnerSubject = "reader-1",
            MediaGuid = MediaGuid,
            Liked = true,
            LikedAt = Now,
            UpdatedAt = Now
        };
}
