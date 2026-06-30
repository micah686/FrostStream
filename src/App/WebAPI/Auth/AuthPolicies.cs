using Microsoft.AspNetCore.Authorization;

namespace WebAPI.Auth;

/// <summary>
/// Named authorization policies applied across FrostStream controllers. Per-endpoint authorization
/// is dynamic: actions carry <see cref="EndpointAttribute"/> and the policy is resolved by
/// <see cref="EndpointPolicyProvider"/> into an OpenFGA <c>invoke</c> check on
/// <c>endpoint:&lt;id&gt;</c>. The only static policy is <see cref="Authenticated"/>.
/// </summary>
public static class AuthPolicies
{
    /// <summary>
    /// Authentication only, with no OpenFGA check. Used by the session-sync endpoint, which is what
    /// bootstraps a user's tuples — requiring an endpoint <c>invoke</c> check there would 403 every
    /// first-time login before any tuple exists.
    /// </summary>
    public const string Authenticated = "Authenticated";

    public static void AddFrostStreamPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(Authenticated, new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());

        // Fail closed: any route that does not opt in with [Endpoint], [Authorize(Authenticated)],
        // or [AllowAnonymous] is denied. A newly-added route is locked out until it declares an id
        // and is seeded, rather than silently inheriting broad access.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => false)
            .Build();
    }
}
