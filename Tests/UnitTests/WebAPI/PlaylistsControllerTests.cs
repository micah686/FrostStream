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

public sealed class PlaylistsControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 12, 15);

    [Test]
    public async Task Submit_Publishes_PlaylistRequested_With_Default_Storage()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        var result = await controller.Submit(new PlaylistRequest
        {
            SourceUrl = "https://example.test/playlist",
            StorageKey = ""
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<PlaylistRequestResponse>();
        payload.PlaylistId.ShouldNotBe(Guid.Empty);
        payload.CorrelationId.ShouldNotBe(Guid.Empty);

        await publisher.Received(1).PublishAsync(
            PlaylistSubjects.PlaylistRequested,
            Arg.Is<PlaylistRequested>(x =>
                x.PlaylistId == payload.PlaylistId &&
                x.CorrelationId == payload.CorrelationId &&
                x.CausationId == null &&
                x.OperationKey == $"playlist/{payload.PlaylistId:N}/requested" &&
                x.OccurredAt == Now &&
                x.Attempt == 1 &&
                x.SourceUrl == "https://example.test/playlist" &&
                x.StorageKey == "default" &&
                x.RequestedBy == "micah"),
            Arg.Is<string>(x => x.Length == 32),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Sends_Query_And_Returns_Items()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);
        var playlist = new PlaylistDto
        {
            PlaylistId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            State = PlaylistState.MetadataResolved,
            SourceUrl = "https://example.test/playlist",
            StorageKey = "storage-a",
            CreatedAt = Now,
            UpdatedAt = Now
        };

        bus.RequestAsync<PlaylistListRequestMessage, PlaylistListResponseMessage>(
                PlaylistSubjects.PlaylistList,
                Arg.Is<PlaylistListRequestMessage>(x => x.PageSize == 25 && x.PageOffset == 50),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlaylistListResponseMessage { Success = true, Items = [playlist] });

        var result = await controller.List(25, 50, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeAssignableTo<IReadOnlyList<PlaylistDto>>();
        payload.ShouldNotBeNull();
        payload.Single().PlaylistId.ShouldBe(playlist.PlaylistId);
    }

    [Test]
    public async Task GetById_Maps_NotFound_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);
        var playlistId = Guid.NewGuid();

        bus.RequestAsync<PlaylistGetRequestMessage, PlaylistGetResponseMessage>(
                PlaylistSubjects.PlaylistGet,
                Arg.Is<PlaylistGetRequestMessage>(x => x.PlaylistId == playlistId),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlaylistGetResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.GetById(playlistId, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    [Test]
    public async Task Submit_Returns_502_When_Publish_Fails()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(),
                Arg.Any<PlaylistRequested>(),
                Arg.Any<string>(),
                null,
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("nats unavailable"));
        var controller = CreateController(publisher: publisher);

        var result = await controller.Submit(new PlaylistRequest
        {
            SourceUrl = "https://example.test/playlist"
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task Submit_Rejects_Localhost_Source_Url()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        var result = await controller.Submit(new PlaylistRequest
        {
            SourceUrl = "http://localhost:8080/playlist"
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default!,
            default(PlaylistRequested)!,
            default,
            default,
            default);
    }

    private static PlaylistsController CreateController(
        IJetStreamPublisher? publisher = null,
        IMessageBus? bus = null)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        var controller = new PlaylistsController(
            publisher ?? Substitute.For<IJetStreamPublisher>(),
            bus ?? Substitute.For<IMessageBus>(),
            clock,
            Substitute.For<ILogger<PlaylistsController>>());

        // The controller stamps RequestedBy from the validated token subject, so give it one.
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, "unit_test_user")], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }
}
