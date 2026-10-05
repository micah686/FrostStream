using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Shared.Auth;

namespace WebAPI.Auth;

/// <summary>
/// Declares the stable id of the endpoint an action serves. Authorization for the action becomes an
/// OpenFGA <c>invoke</c> check on <c>endpoint:&lt;id&gt;</c>. The id must exist in
/// <see cref="EndpointCatalog"/> (enforced at startup by <see cref="EndpointCatalogValidator"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class EndpointAttribute : AuthorizeAttribute
{
    public EndpointAttribute(string endpointId)
    {
        EndpointId = endpointId;
        Policy = EndpointPolicy.PolicyName(endpointId);
    }

    public string EndpointId { get; }
}

/// <summary>Naming helpers for the dynamically-resolved per-endpoint authorization policies.</summary>
public static class EndpointPolicy
{
    public const string Prefix = "fs.endpoint:";

    public static string PolicyName(string endpointId) => Prefix + endpointId;

    public static bool TryGetEndpointId(string policyName, out string endpointId)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            endpointId = policyName[Prefix.Length..];
            return true;
        }

        endpointId = string.Empty;
        return false;
    }
}

/// <summary>
/// Resolves <c>fs.endpoint:&lt;id&gt;</c> policy names into a policy that requires an authenticated
/// user plus an OpenFGA <c>invoke</c> check on <c>endpoint:&lt;id&gt;</c>. Every other policy name
/// (e.g. <see cref="AuthPolicies.Authenticated"/>) and the default/fallback policy fall through to
/// the framework's default provider, so the session-sync bootstrap exemption survives untouched.
/// </summary>
public sealed class EndpointPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public EndpointPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (EndpointPolicy.TryGetEndpointId(policyName, out var endpointId))
        {
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new FrostStreamPermissionRequirement(
                    AuthConstants.InvokeRelation,
                    AuthConstants.EndpointObject(endpointId)))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
