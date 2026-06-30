using System.Text.RegularExpressions;
using Shared.Secrets;

namespace Shared.Messaging;

public static partial class NotificationProfileValidator
{
    public static readonly IReadOnlySet<string> SupportedProviderKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "email",
        "sms",
        "push",
        "whatsapp",
        "slack",
        "discord",
        "teams",
        "telegram",
        "facebook",
        "line",
        "viber",
        "mattermost",
        "rocketchat"
    };

    public static string? Validate(NotificationPreferencesDto preferences)
    {
        var providerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in preferences.Providers)
        {
            if (Validate(provider) is { } providerError)
                return providerError;
            if (!providerKeys.Add(provider.ProviderKey))
                return $"Duplicate notification provider key '{provider.ProviderKey}'.";
        }

        foreach (var rule in preferences.Rules)
        {
            if (!NotificationEventKeys.Supported.Contains(rule.EventKey))
                return $"Unsupported notification event '{rule.EventKey}'.";
            foreach (var providerKey in rule.ProviderKeys)
            {
                if (!providerKeys.Contains(providerKey))
                    return $"Notification rule '{rule.EventKey}' references unknown provider '{providerKey}'.";
            }
        }

        return null;
    }

    public static string? Validate(NotificationProviderDto provider)
    {
        if (!SecretPaths.IsValidProfileKey(provider.ProviderKey))
            return "Provider key must match ^[a-z0-9-]{2,100}$.";
        if (!SupportedProviderKinds.Contains(provider.ProviderKind))
            return $"Unsupported notification provider kind '{provider.ProviderKind}'.";
        if (provider.NotifyConfig.ValueKind is not System.Text.Json.JsonValueKind.Object)
            return "notifyConfig must be a JSON object.";

        return ValidateSecretReferences(provider.ProviderKey, provider.NotifyConfig);
    }

    public static string? ValidateSecretReferences(string providerKey, System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ValidateSecretReferences(providerKey, property.Value) is { } error)
                        return error;
                }

                return null;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ValidateSecretReferences(providerKey, item) is { } error)
                        return error;
                }

                return null;
            case System.Text.Json.JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("secret://", StringComparison.Ordinal))
                    return null;

                var match = SecretReferenceRegex().Match(value);
                if (!match.Success)
                    return $"Invalid notification secret reference '{value}'.";
                if (!string.Equals(match.Groups["provider"].Value, providerKey, StringComparison.Ordinal))
                    return $"Secret reference '{value}' must use provider key '{providerKey}'.";
                if (!SecretPaths.IsValidNotificationSecretName(match.Groups["secret"].Value))
                    return $"Invalid notification secret name in reference '{value}'.";

                return null;
            default:
                return null;
        }
    }

    public static bool TryParseSecretReference(string value, out string providerKey, out string secretName)
    {
        var match = SecretReferenceRegex().Match(value);
        if (match.Success)
        {
            providerKey = match.Groups["provider"].Value;
            secretName = match.Groups["secret"].Value;
            return true;
        }

        providerKey = string.Empty;
        secretName = string.Empty;
        return false;
    }

    [GeneratedRegex("^secret://(?<provider>[a-z0-9-]{2,100})/(?<secret>[A-Za-z0-9_.-]{1,100})$")]
    private static partial Regex SecretReferenceRegex();
}
