using System.Text.Json;
using Conduit.NATS;
using Shared.Messaging;
using Shared.Secrets;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.DownloadConfigSets;

public sealed record ResolvedDownloadConfigSet(
    string? ConfigSetKey,
    string StorageKey,
    string? CookieSecretPath,
    YtDlpOptions? YtDlpOptions,
    bool EncodeForPlaylist,
    int Priority,
    bool FetchComments);

public static class DownloadConfigSetResolver
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public static async Task<(ResolvedDownloadConfigSet? Config, string? Error)> ResolveAsync(
        IMessageBus messageBus,
        string? ownerSubject,
        string? configSetKey,
        string? storageKeyOverride,
        string? cookieProfileKeyOverride,
        YtDlpOptions? ytDlpOptionsOverride,
        bool? encodeForPlaylistOverride,
        int? priorityOverride,
        bool? fetchCommentsOverride,
        CancellationToken cancellationToken)
    {
        DownloadConfigSetDto? config = null;
        if (!string.IsNullOrWhiteSpace(configSetKey))
        {
            if (string.IsNullOrWhiteSpace(ownerSubject))
                return (null, "An authenticated user is required to use a download config set.");

            var response = await messageBus.RequestAsync<DownloadConfigSetResolveRequestMessage, DownloadConfigSetOperationResponseMessage>(
                DownloadConfigSetSubjects.Resolve,
                new DownloadConfigSetResolveRequestMessage { OwnerSubject = ownerSubject, Key = configSetKey.Trim() },
                RequestTimeout,
                cancellationToken);

            if (response?.Success != true || response.Entity is null)
                return (null, response?.ErrorMessage ?? $"Download config set '{configSetKey}' was not found.");

            config = response.Entity;
        }

        var cookieProfileKey = Normalize(cookieProfileKeyOverride) ?? config?.CookieProfileKey;
        string? cookieSecretPath = null;
        if (!string.IsNullOrWhiteSpace(cookieProfileKey))
        {
            if (!SecretPaths.IsValidUserScope(ownerSubject) || !SecretPaths.IsValidProfileKey(cookieProfileKey))
                return (null, "cookieProfileKey must match ^[a-z0-9-]{2,100}$ for an authenticated user.");

            cookieSecretPath = SecretPaths.ForUserCookieProfile(ownerSubject!, cookieProfileKey);
        }

        var storageKey = Normalize(storageKeyOverride) ?? config?.StorageKey ?? "default";
        var ytDlpOptions = ytDlpOptionsOverride ?? Deserialize(config?.YtDlpOptionsJson);

        // encodeForPlaylist/fetchComments are no longer config-set fields (removed —
        // they duplicated settings the caller's own request body already carries); these always
        // resolve from the caller-supplied override, with no config-set-level fallback.
        return (new ResolvedDownloadConfigSet(
            ConfigSetKey: Normalize(configSetKey),
            StorageKey: storageKey,
            CookieSecretPath: cookieSecretPath,
            YtDlpOptions: ytDlpOptions,
            EncodeForPlaylist: encodeForPlaylistOverride ?? false,
            Priority: priorityOverride ?? config?.Priority ?? 0,
            FetchComments: fetchCommentsOverride ?? false), null);
    }

    private static YtDlpOptions? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<YtDlpOptions>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
