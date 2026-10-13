using Shared.Auth;

namespace WebAPI.Auth;

public sealed class AllowAllAuthorizer : IFrostStreamAuthorizer
{
    public Task<FrostStreamAuthorizationDecision> CheckAsync(
        FrostStreamAuthorizationCheck check,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FrostStreamAuthorizationDecision.Permit());
}
