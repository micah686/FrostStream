using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

/// <summary>
/// Single-user mode wires <see cref="AllowAllAuthorizer"/> as the <see cref="IFrostStreamAuthorizer"/>,
/// so every authorization check must pass without any external store.
/// </summary>
public sealed class AllowAllAuthorizerTests
{
    [Test]
    public async Task Permits_Any_Check()
    {
        var authorizer = new AllowAllAuthorizer();

        var decision = await authorizer.CheckAsync(new FrostStreamAuthorizationCheck(
            "user:anyone",
            AuthConstants.InvokeRelation,
            AuthConstants.EndpointObject("downloads.create")));

        decision.Allowed.ShouldBeTrue();
    }
}
