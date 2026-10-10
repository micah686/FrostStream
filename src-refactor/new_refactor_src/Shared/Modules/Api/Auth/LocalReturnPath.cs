namespace WebAPI.Auth;

public static class LocalReturnPath
{
    public static string Normalize(string? value, string fallback = "/profile")
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\r') ||
            value.Contains('\n'))
        {
            return fallback;
        }

        return Uri.TryCreate(value, UriKind.Relative, out _) ? value : fallback;
    }
}
