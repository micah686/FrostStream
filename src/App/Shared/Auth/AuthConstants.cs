using System.Security.Claims;

namespace Shared.Auth;

public static class AuthConstants
{
    public const string SingleUserScheme = "SingleUser";
    public const string SingleUserSubject = "single-user-owner";
    public static readonly Guid SingleUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public const string SystemObject = "system:froststream";
    public const string AccessRelation = "access";
    public const string OwnerRelation = "owner";
    public const string AdminRelation = "admin";

    public const string SubjectClaim = "sub";
    public const string GroupsClaim = "groups";
    public const string PreferredUsernameClaim = "preferred_username";

    public static string? FindSubject(ClaimsPrincipal principal)
        => FirstNonBlank(
            principal.FindFirst(SubjectClaim)?.Value,
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            principal.Identity?.Name);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
