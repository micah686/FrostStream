using System.Security.Claims;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Playlists.Controllers;
using WebAPI.Features.Playlists.Models;

namespace UnitTests.WebAPI;

public sealed class UserPlaylistsControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 4, 8, 0);

    [Test]
    public async Task Create_Sends_Owner_Scoped_Request()
    {
        var bus = Substitute.For<IMessageBus>();
        var playlistId = Guid.NewGuid();
        bus.RequestAsync<UserPlaylistCreateRequestMessage, UserPlaylistResponseMessage>(
                PlaylistSubjects.UserPlaylistCreate,
                Arg.Any<UserPlaylistCreateRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserPlaylistResponseMessage
            {
                Success = true,
                Playlist = new UserPlaylistDto
                {
                    PlaylistId = playlistId,
                    Name = "Watch later",
                    CreatedAt = Now,
                    UpdatedAt = Now
                }
            });
        var controller = CreateController(bus);

        var result = await controller.Create(new UserPlaylistCreateRequest
        {
            Name = "Watch later",
            Description = "private queue"
        }, CancellationToken.None);

        var created = result.Result.ShouldBeOfType<CreatedAtActionResult>();
        created.Value.ShouldBeOfType<UserPlaylistDto>().PlaylistId.ShouldBe(playlistId);
        await bus.Received(1).RequestAsync<UserPlaylistCreateRequestMessage, UserPlaylistResponseMessage>(
            PlaylistSubjects.UserPlaylistCreate,
            Arg.Is<UserPlaylistCreateRequestMessage>(x =>
                x.OwnerSubject == "unit_test_user" &&
                x.Name == "Watch later" &&
                x.Description == "private queue"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Get_Maps_NotFound()
    {
        var bus = Substitute.For<IMessageBus>();
        var playlistId = Guid.NewGuid();
        bus.RequestAsync<UserPlaylistGetRequestMessage, UserPlaylistResponseMessage>(
                PlaylistSubjects.UserPlaylistGet,
                Arg.Is<UserPlaylistGetRequestMessage>(x =>
                    x.OwnerSubject == "unit_test_user" &&
                    x.PlaylistId == playlistId),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserPlaylistResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });
        var controller = CreateController(bus);

        var result = await controller.Get(playlistId, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    [Test]
    public async Task Create_Without_Subject_Returns_Unauthorized()
    {
        var controller = CreateController(Substitute.For<IMessageBus>(), subject: null);

        var result = await controller.Create(new UserPlaylistCreateRequest { Name = "Private" }, CancellationToken.None);

        result.Result.ShouldBeOfType<UnauthorizedResult>();
    }

    private static UserPlaylistsController CreateController(IMessageBus bus, string? subject = "unit_test_user")
    {
        var controller = new UserPlaylistsController(
            bus,
            Substitute.For<ILogger<UserPlaylistsController>>());

        var identity = subject is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, subject)], "test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }
}
