using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Shared.Auth;

namespace WebAPI.Auth;

/// <summary>Lite grants backend access centrally, including policies added by future endpoints.</summary>
public sealed class LiteAuthorizationService : IAuthorizationService
{
    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
        IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Success());

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
        string policyName) => Task.FromResult(AuthorizationResult.Success());
}

public sealed class LiteAuthorizationPolicyProvider : IAuthorizationPolicyProvider
{
    private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder(AuthConstants.SingleUserScheme)
        .RequireAssertion(_ => true).Build();

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(Policy);
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(Policy);
}

/// <summary>Explicit endpoint schemes and old credentials still resolve to Lite's local identity.</summary>
public sealed class LitePolicyEvaluator(IAuthorizationService authorization) : IPolicyEvaluator
{
    public async Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
    {
        var result = await context.AuthenticateAsync(AuthConstants.SingleUserScheme);
        if (result.Principal is not null) context.User = result.Principal;
        return result;
    }

    public async Task<PolicyAuthorizationResult> AuthorizeAsync(AuthorizationPolicy policy,
        AuthenticateResult authenticationResult, HttpContext context, object? resource)
    {
        var result = await authorization.AuthorizeAsync(context.User, resource, policy.Requirements);
        return result.Succeeded ? PolicyAuthorizationResult.Success() : PolicyAuthorizationResult.Forbid();
    }
}

public sealed class LiteMediaAccessChecker : WebAPI.Features.Media.IMediaAccessChecker
{
    public Task<Microsoft.AspNetCore.Mvc.IActionResult?> CheckWatchAccessAsync(ClaimsPrincipal? user,
        Guid mediaGuid, CancellationToken cancellationToken) => Task.FromResult<Microsoft.AspNetCore.Mvc.IActionResult?>(null);
}
