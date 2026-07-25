using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shared.Secrets;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Cookies.Controllers;
using WebAPI.Features.Cookies.Models;

namespace UnitTests.WebAPI;

public sealed class CookiesControllerTests
{
    private const string Subject = "user-123";

    [Test]
    public async Task Upsert_Writes_Cookie_Content_To_User_Scoped_Path()
    {
        var store = Substitute.For<ISecretStore>();
        var bus = Substitute.For<IMessageBus>();
        StubUpsert(bus, "member-cookie");
        var controller = CreateController(store, bus, Subject);

        var result = await controller.Upsert("member-cookie", new CookieUpsertRequest
        {
            Content = "# Netscape HTTP Cookie File",
            Site = "example.com"
        }, CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CookieProfileResponse>().ProfileKey.ShouldBe("member-cookie");
        await store.Received(1).WriteAsync(
            "cookies/users/user-123/member-cookie",
            Arg.Is<IReadOnlyDictionary<string, string>>(x => x != null && x["content"] == "# Netscape HTTP Cookie File"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upsert_Is_Scoped_To_The_Authenticated_Subject()
    {
        // A different caller addressing the same profile key writes to a different, isolated path —
        // cross-user cookie access is impossible because the path is derived from the token subject.
        var store = Substitute.For<ISecretStore>();
        var bus = Substitute.For<IMessageBus>();
        StubUpsert(bus, "member-cookie");
        var controller = CreateController(store, bus, "user-999");

        await controller.Upsert("member-cookie", new CookieUpsertRequest { Content = "x" }, CancellationToken.None);

        await store.Received(1).WriteAsync(
            "cookies/users/user-999/member-cookie",
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upsert_Rejects_Invalid_Key_And_Does_Not_Write()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store, Substitute.For<IMessageBus>(), Subject);

        var result = await controller.Upsert("Bad_Key", new CookieUpsertRequest { Content = "content" }, CancellationToken.None);

        result.Result!.ShouldBeOfType<BadRequestObjectResult>();
        await store.DidNotReceive().WriteAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upsert_Without_Subject_Returns_Unauthorized()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store, Substitute.For<IMessageBus>(), subject: null);

        var result = await controller.Upsert("member-cookie", new CookieUpsertRequest { Content = "x" }, CancellationToken.None);

        result.Result!.ShouldBeOfType<UnauthorizedResult>();
    }

    [Test]
    public async Task Get_Returns_Metadata_When_Profile_Exists()
    {
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<CookieProfileGetRequestMessage, CookieProfileOperationResponseMessage>(
                CookieProfileSubjects.Get, Arg.Any<CookieProfileGetRequestMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new CookieProfileOperationResponseMessage { Success = true, Entity = Dto("member-cookie") });
        var controller = CreateController(Substitute.For<ISecretStore>(), bus, Subject);

        var result = await controller.Get("member-cookie", CancellationToken.None);

        result.Result!.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CookieProfileResponse>().ProfileKey.ShouldBe("member-cookie");
    }

    [Test]
    public async Task Get_Returns_NotFound_When_Profile_Missing()
    {
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<CookieProfileGetRequestMessage, CookieProfileOperationResponseMessage>(
                CookieProfileSubjects.Get, Arg.Any<CookieProfileGetRequestMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new CookieProfileOperationResponseMessage { Success = false, ErrorCode = "not_found" });
        var controller = CreateController(Substitute.For<ISecretStore>(), bus, Subject);

        var result = await controller.Get("member-cookie", CancellationToken.None);

        result.Result!.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task Delete_Removes_Secret_And_Returns_NoContent()
    {
        var store = Substitute.For<ISecretStore>();
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<CookieProfileDeleteRequestMessage, CookieProfileOperationResponseMessage>(
                CookieProfileSubjects.Delete, Arg.Any<CookieProfileDeleteRequestMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new CookieProfileOperationResponseMessage { Success = true });
        var controller = CreateController(store, bus, Subject);

        var result = await controller.Delete("member-cookie", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        await store.Received(1).DeleteAsync("cookies/users/user-123/member-cookie", Arg.Any<CancellationToken>());
    }

    private static void StubUpsert(IMessageBus bus, string profileKey)
        => bus.RequestAsync<CookieProfileUpsertRequestMessage, CookieProfileOperationResponseMessage>(
                CookieProfileSubjects.Upsert, Arg.Any<CookieProfileUpsertRequestMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new CookieProfileOperationResponseMessage { Success = true, Entity = Dto(profileKey) });

    private static CookieProfileDto Dto(string profileKey) => new()
    {
        Id = Guid.NewGuid(),
        OwnerSubject = Subject,
        ProfileKey = profileKey,
        CreatedAt = SystemClock.Instance.GetCurrentInstant()
    };

    private static CookiesController CreateController(ISecretStore store, IMessageBus bus, string? subject)
    {
        var controller = new CookiesController(store, bus, Substitute.For<ILogger<CookiesController>>());

        var identity = subject is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(new[] { new Claim(AuthConstants.SubjectClaim, subject) }, "Test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }
}
