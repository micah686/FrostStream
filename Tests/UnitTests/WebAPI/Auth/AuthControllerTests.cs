using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;
using WebAPI.Features.Auth.Controllers;

namespace UnitTests.WebAPI.Auth;

public sealed class AuthControllerTests
{
    [Test]
    public async Task SyncSession_Upserts_The_Subject_And_Syncs_Group_Tuples()
    {
        var userId = Guid.NewGuid();
        UserSessionUpsertRequestMessage? captured = null;

        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(
                UserSessionSubjects.Upsert,
                Arg.Do<UserSessionUpsertRequestMessage>(m => captured = m),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserSessionUpsertResponseMessage { Success = true, UserId = userId });

        var tupleWriter = new RecordingTupleWriter();
        var controller = BuildController(bus, tupleWriter, new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthConstants.SubjectClaim, "auth0|abc"),
            new Claim(AuthConstants.PreferredUsernameClaim, "micah"),
            new Claim(AuthConstants.GroupsClaim, "admins"),
            new Claim(AuthConstants.GroupsClaim, "viewers")
        ], "test")));

        var result = await controller.SyncSession(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var body = ok.Value.ShouldBeOfType<AuthSessionResponse>();
        body.UserId.ShouldBe(userId);
        body.Subject.ShouldBe("auth0|abc");
        body.Groups.ShouldBe(["admins", "viewers"]);

        // Identity comes from the validated token, not client free-text.
        captured.ShouldNotBeNull();
        captured!.Subject.ShouldBe("auth0|abc");
        captured.DisplayName.ShouldBe("micah");
        captured.Groups.ShouldBe(["admins", "viewers"]);

        // Group membership is reconciled into OpenFGA tuples for the same subject.
        tupleWriter.Calls.ShouldBe(1);
        tupleWriter.Subject.ShouldBe("auth0|abc");
        tupleWriter.Groups.ShouldBe(["admins", "viewers"]);
    }

    [Test]
    public async Task SyncSession_Returns_Unauthorized_When_No_Subject()
    {
        var bus = Substitute.For<IMessageBus>();
        var tupleWriter = new RecordingTupleWriter();
        var controller = BuildController(bus, tupleWriter,
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")));

        var result = await controller.SyncSession(CancellationToken.None);

        result.Result.ShouldBeOfType<UnauthorizedResult>();
        tupleWriter.Calls.ShouldBe(0);
        await bus.DidNotReceiveWithAnyArgs().RequestAsync<UserSessionUpsertRequestMessage, UserSessionUpsertResponseMessage>(
            default!, default!, default, default);
    }

    [Test]
    public void SyncSession_Uses_The_Authenticated_Policy_Not_An_Endpoint_Invoke_Check()
    {
        // Regression guard: the session-sync endpoint bootstraps the caller's tuples, so it must only
        // require authentication. An OpenFGA `invoke` check here would 403 every first-time login.
        var attribute = typeof(AuthController)
            .GetMethod(nameof(AuthController.SyncSession))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ShouldHaveSingleItem();

        attribute.Policy.ShouldBe(AuthPolicies.Authenticated);
    }

    private static AuthController BuildController(IMessageBus bus, IOpenFgaTupleWriter tupleWriter, ClaimsPrincipal user)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var synchronization = new SessionSynchronizationService(
            bus,
            tupleWriter,
            NullLogger<SessionSynchronizationService>.Instance);
        return new AuthController(configuration, synchronization, Substitute.For<IAntiforgery>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }
}
