using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;
using Shared.Application;
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
        var application = Substitute.For<IUserNoteApplication>();
        var controller = CreateController(application, "micah");
        var mediaGuid = Guid.NewGuid();

        application.UpsertAsync(
                Arg.Is<UserNoteUpsertRequestMessage>(x => x != null &&
                    x.OwnerSubject == "micah" &&
                    x.TargetType == "video" &&
                    x.TargetId == mediaGuid.ToString() &&
                    x.Note == "remember this"),
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

        result.Result!.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<UserNoteDto>().Note.ShouldBe("remember this");
    }

    [Test]
    public async Task Search_Rejects_Blank_Query_And_Requires_User()
    {
        var application = Substitute.For<IUserNoteApplication>();
        var controller = CreateController(application, "micah");

        var blank = await controller.Search(" ", cancellationToken: CancellationToken.None);
        blank.Result!.ShouldBeOfType<BadRequestObjectResult>().Value!.ShouldBe("Query parameter 'q' is required.");

        var anonymous = await CreateController(application, subject: null).Search("needle", cancellationToken: CancellationToken.None);
        anonymous.Result!.ShouldBeOfType<UnauthorizedResult>();

        await application.DidNotReceive().SearchAsync(
            Arg.Any<UserNoteSearchRequestMessage>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Sends_Paginated_Request_With_Blank_Query()
    {
        var application = Substitute.For<IUserNoteApplication>();
        var controller = CreateController(application, "micah");

        application.SearchAsync(
                Arg.Is<UserNoteSearchRequestMessage>(x => x != null &&
                    x.OwnerSubject == "micah" &&
                    x.Query == string.Empty &&
                    x.TargetType == "video" &&
                    x.PageSize == 25 &&
                    x.PageOffset == 50),
                Arg.Any<CancellationToken>())
            .Returns(new UserNoteSearchResponseMessage { Success = true });

        var result = await controller.List("video", 25, 50, CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<UserNoteSearchResponseMessage>().Success.ShouldBeTrue();
    }

    [Test]
    public async Task Delete_Is_Idempotent_When_Note_Is_Missing()
    {
        var application = Substitute.For<IUserNoteApplication>();
        var controller = CreateController(application, "micah");

        application.DeleteAsync(
                Arg.Any<UserNoteDeleteRequestMessage>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserNoteResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Delete("channel", "123", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    private static UserNotesController CreateController(IUserNoteApplication application, string? subject)
    {
        var currentOwner = Substitute.For<ICurrentOwner>();
        currentOwner.Subject.Returns(subject);
        return new UserNotesController(application, currentOwner);
    }
}
