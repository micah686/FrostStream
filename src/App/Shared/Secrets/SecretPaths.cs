using System.Text.RegularExpressions;

namespace Shared.Secrets;

public static partial class SecretPaths
{
    public static string ForStorage(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        }

        return $"storage/{storageKey}";
    }

    /// <summary>
    /// Legacy global cookie path (<c>cookies/{key}</c>). Retained for backwards compatibility;
    /// new cookie profiles are user-scoped via <see cref="ForUserCookieProfile"/>.
    /// </summary>
    public static string ForCookies(string cookieKey)
    {
        if (string.IsNullOrWhiteSpace(cookieKey))
        {
            throw new ArgumentException("Cookie key is required.", nameof(cookieKey));
        }

        return $"cookies/{cookieKey}";
    }

    /// <summary>
    /// User-scoped cookie profile path (<c>cookies/users/{userScope}/{profileKey}</c>). The scope is
    /// derived from the authenticated subject, so a user can only ever address their own profiles.
    /// </summary>
    public static string ForUserCookieProfile(string userScope, string profileKey)
    {
        if (!IsValidUserScope(userScope))
        {
            throw new ArgumentException("User scope must match ^[A-Za-z0-9_.-]{1,128}$.", nameof(userScope));
        }

        if (!IsValidProfileKey(profileKey))
        {
            throw new ArgumentException("Cookie profile key must match ^[a-z0-9-]{2,100}$.", nameof(profileKey));
        }

        return $"cookies/users/{userScope}/{profileKey}";
    }

    public static bool IsValidUserScope(string? userScope)
        => !string.IsNullOrWhiteSpace(userScope) && UserScopeRegex().IsMatch(userScope);

    public static bool IsValidProfileKey(string? profileKey)
        => !string.IsNullOrWhiteSpace(profileKey) && ProfileKeyRegex().IsMatch(profileKey);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex UserScopeRegex();

    [GeneratedRegex("^[a-z0-9-]{2,100}$")]
    private static partial Regex ProfileKeyRegex();
}
