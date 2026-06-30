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

    /// <summary>
    /// User-scoped notification provider secret document
    /// (<c>notifications/users/{userScope}/{providerKey}</c>). Provider config JSON stores only
    /// <c>secret://{providerKey}/{secretName}</c> references; secret values live as fields here.
    /// </summary>
    public static string ForUserNotificationProvider(string userScope, string providerKey)
    {
        if (!IsValidUserScope(userScope))
        {
            throw new ArgumentException("User scope must match ^[A-Za-z0-9_.-]{1,128}$.", nameof(userScope));
        }

        if (!IsValidProfileKey(providerKey))
        {
            throw new ArgumentException("Notification provider key must match ^[a-z0-9-]{2,100}$.", nameof(providerKey));
        }

        return $"notifications/users/{userScope}/{providerKey}";
    }

    public static string ForUserNotificationSecret(string userScope, string providerKey, string secretName)
    {
        var providerPath = ForUserNotificationProvider(userScope, providerKey);
        if (!IsValidNotificationSecretName(secretName))
        {
            throw new ArgumentException("Notification secret name must match ^[A-Za-z0-9_.-]{1,100}$.", nameof(secretName));
        }

        return $"{providerPath}/{secretName}";
    }

    public static bool IsValidUserScope(string? userScope)
        => !string.IsNullOrWhiteSpace(userScope) && UserScopeRegex().IsMatch(userScope);

    public static bool IsValidProfileKey(string? profileKey)
        => !string.IsNullOrWhiteSpace(profileKey) && ProfileKeyRegex().IsMatch(profileKey);

    public static bool IsValidNotificationSecretName(string? secretName)
        => !string.IsNullOrWhiteSpace(secretName) && NotificationSecretNameRegex().IsMatch(secretName);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex UserScopeRegex();

    [GeneratedRegex("^[a-z0-9-]{2,100}$")]
    private static partial Regex ProfileKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex NotificationSecretNameRegex();
}
