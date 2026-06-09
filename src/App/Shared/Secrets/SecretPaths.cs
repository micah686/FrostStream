namespace Shared.Secrets;

public static class SecretPaths
{
    public static string ForStorage(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        }

        return $"storage/{storageKey}";
    }

    public static string ForCookies(string cookieKey)
    {
        if (string.IsNullOrWhiteSpace(cookieKey))
        {
            throw new ArgumentException("Cookie key is required.", nameof(cookieKey));
        }

        return $"cookies/{cookieKey}";
    }
}
