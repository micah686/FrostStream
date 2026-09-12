using Shared.Auth;

namespace Shared.Application;

/// <summary>
/// Resolves the application owner for the current operation independently of authentication.
/// Full supplies the authenticated HTTP subject; Lite supplies the stable single-user owner.
/// </summary>
public interface ICurrentOwner
{
    string? Subject { get; }
}

public sealed class FixedCurrentOwner : ICurrentOwner
{
    public string Subject => AuthConstants.SingleUserSubject;
}
