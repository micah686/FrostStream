using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

/// <summary>
/// The handler translates the authenticated principal's subject into the OpenFGA check
/// <c>user:&lt;sub&gt;</c> + relation + object and succeeds/fails on the authorizer's decision.
/// </summary>
public sealed class FrostStreamPermissionHandlerTests
{
    private static readonly FrostStreamPermissionRequirement Requirement =
        new(AuthConstants.InvokeRelation, AuthConstants.EndpointObject("downloads.create"));

    [Test]
    public async Task Maps_Subject_To_User_And_Succeeds_When_Permitted()
    {
        var authorizer = new RecordingAuthorizer(FrostStreamAuthorizationDecision.Permit());
        var context = ContextFor(authorizer, WithSubject("auth0|abc"));

        await new FrostStreamPermissionHandler(authorizer, NullLogger<FrostStreamPermissionHandler>.Instance)
            .HandleAsync(context);

        context.HasSucceeded.ShouldBeTrue();
        authorizer.LastCheck.ShouldNotBeNull();
        authorizer.LastCheck!.User.ShouldBe("user:auth0|abc");
        authorizer.LastCheck.Relation.ShouldBe(AuthConstants.InvokeRelation);
        authorizer.LastCheck.Object.ShouldBe(AuthConstants.EndpointObject("downloads.create"));
    }

    [Test]
    public async Task Fails_When_Authorizer_Denies()
    {
        var authorizer = new RecordingAuthorizer(FrostStreamAuthorizationDecision.Deny("nope"));
        var context = ContextFor(authorizer, WithSubject("abc"));

        await new FrostStreamPermissionHandler(authorizer, NullLogger<FrostStreamPermissionHandler>.Instance)
            .HandleAsync(context);

        context.HasSucceeded.ShouldBeFalse();
        context.HasFailed.ShouldBeTrue();
    }

    [Test]
    public async Task Fails_Without_Calling_Authorizer_When_No_Subject()
    {
        var authorizer = new RecordingAuthorizer(FrostStreamAuthorizationDecision.Permit());
        // Authenticated identity but no subject/name claim.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));
        var context = ContextFor(authorizer, anonymous);

        await new FrostStreamPermissionHandler(authorizer, NullLogger<FrostStreamPermissionHandler>.Instance)
            .HandleAsync(context);

        context.HasFailed.ShouldBeTrue();
        authorizer.LastCheck.ShouldBeNull();
    }

    private static AuthorizationHandlerContext ContextFor(IFrostStreamAuthorizer _, ClaimsPrincipal user)
        => new([Requirement], user, resource: null);

    private static ClaimsPrincipal WithSubject(string subject)
        => new(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, subject)], "test"));
}
