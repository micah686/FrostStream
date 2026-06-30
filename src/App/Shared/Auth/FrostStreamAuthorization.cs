namespace Shared.Auth;

public sealed record FrostStreamAuthorizationCheck(
    string User,
    string Relation,
    string Object);

public sealed record FrostStreamAuthorizationDecision(
    bool Allowed,
    string? Reason = null)
{
    public static FrostStreamAuthorizationDecision Permit() => new(true);

    public static FrostStreamAuthorizationDecision Deny(string reason) => new(false, reason);
}

public interface IFrostStreamAuthorizer
{
    Task<FrostStreamAuthorizationDecision> CheckAsync(
        FrostStreamAuthorizationCheck check,
        CancellationToken cancellationToken = default);
}
