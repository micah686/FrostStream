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
using WebAPI.Features.Notes.Controllers;
using WebAPI.Features.Notes.Models;

namespace UnitTests.WebAPI;

public sealed class UserNotesControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 22, 0);

    [Test]
    public async Task Upsert_Sends_Owner_Scoped_Request()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus, "micah");
        var mediaGuid = Guid.NewGuid();

        bus.RequestAsync<UserNoteUpsertRequestMessage, UserNoteResponseMessage>(
                UserNoteSubjects.Upsert,
                Arg.Is<UserNoteUpsertRequestMessage>(x =>
                    x.OwnerSubject == "micah" &&
                    x.TargetType == "video" &&
                    x.TargetId == mediaGuid.ToString() &&
                    x.Note == "remember this"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserNoteResponseMessage
            {
                Success = true,
                Note = new UserNoteDto
                {
                    TargetType = "video",
                    TargetId = mediaGuid.ToString("N"),
                    Note = "remember this",
                    CreatedAt = Now,
                    UpdatedAt = Now
                }
            });

        var result = await controller.Upsert(
            "video",
            mediaGuid.ToString(),
            new UserNoteUpsertRequest { Note = "remember this" },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<UserNoteDto>().Note.ShouldBe("remember this");
    }

    [Test]
    public async Task Search_Rejects_Blank_Query_And_Requires_User()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus, "micah");

        var blank = await controller.Search(" ", cancellationToken: CancellationToken.None);
        blank.Result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe("Query parameter 'q' is required.");

        var anonymous = await CreateController(bus, subject: null).Search("needle", cancellationToken: CancellationToken.None);
        anonymous.Result.ShouldBeOfType<UnauthorizedResult>();

        await bus.DidNotReceive().RequestAsync<UserNoteSearchRequestMessage, UserNoteSearchResponseMessage>(
            Arg.Any<string>(),
            Arg.Any<UserNoteSearchRequestMessage>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Sends_Paginated_Request_With_Blank_Query()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus, "micah");

        bus.RequestAsync<UserNoteSearchRequestMessage, UserNoteSearchResponseMessage>(
                UserNoteSubjects.Search,
                Arg.Is<UserNoteSearchRequestMessage>(x =>
                    x.OwnerSubject == "micah" &&
                    x.Query == string.Empty &&
                    x.TargetType == "video" &&
                    x.PageSize == 25 &&
                    x.PageOffset == 50),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserNoteSearchResponseMessage { Success = true });

        var result = await controller.List("video", 25, 50, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<UserNoteSearchResponseMessage>().Success.ShouldBeTrue();
    }

    [Test]
    public async Task Delete_Maps_NotFound()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus, "micah");

        bus.RequestAsync<UserNoteDeleteRequestMessage, UserNoteResponseMessage>(
                UserNoteSubjects.Delete,
                Arg.Any<UserNoteDeleteRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserNoteResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Delete("channel", "123", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    private static UserNotesController CreateController(IMessageBus bus, string? subject)
    {
        var controller = new UserNotesController(bus, Substitute.For<ILogger<UserNotesController>>());
        var http = new DefaultHttpContext();
        if (subject is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthConstants.SubjectClaim, subject)],
                authenticationType: "test"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}
