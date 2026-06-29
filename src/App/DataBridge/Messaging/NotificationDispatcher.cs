using System.Text.Json;
using System.Text.Json.Nodes;
using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecurPixel.Notify;
using Shared.Messaging;
using Shared.Secrets;

namespace DataBridge.Messaging;

public interface INotificationDispatcher
{
    Task<NotificationDispatchResult> SendTestAsync(NotificationTestRequestMessage request, CancellationToken cancellationToken = default);

    Task NotifyDownloadOutcomeAsync(Guid jobId, string eventKey, string subject, string body, CancellationToken cancellationToken = default);

    Task NotifyScheduleFailureAsync(string scheduleKey, string failureMessage, CancellationToken cancellationToken = default);
}

public sealed record NotificationDispatchResult(bool Success, string? ErrorCode = null, string? ErrorMessage = null);

public sealed class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    ISecretStore secretStore,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<NotificationDispatchResult> SendTestAsync(
        NotificationTestRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var preferences = await LoadPreferencesAsync(request.OwnerSubject, cancellationToken);
        if (preferences is null)
            return new NotificationDispatchResult(false, "not_found", "User was not found.");

        var provider = preferences.Providers.FirstOrDefault(x =>
            string.Equals(x.ProviderKey, request.ProviderKey, StringComparison.Ordinal));
        if (provider is null)
            return new NotificationDispatchResult(false, "not_found", "Notification provider was not found.");

        return await SendProviderAsync(
            request.OwnerSubject,
            provider,
            "test",
            request.Subject ?? "FrostStream notification test",
            request.Body ?? "This is a test notification from FrostStream.",
            cancellationToken);
    }

    public async Task NotifyDownloadOutcomeAsync(
        Guid jobId,
        string eventKey,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            var job = await db.DownloadJobs
                .AsNoTracking()
                .Where(x => x.JobId == jobId)
                .Select(x => new { x.RequestedBy })
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(job?.RequestedBy))
                return;

            await NotifyUserAsync(job.RequestedBy, eventKey, subject, body, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed dispatching download notification for JobId {JobId}.", jobId);
        }
    }

    public async Task NotifyScheduleFailureAsync(
        string scheduleKey,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            var users = await db.FrostStreamUsers
                .AsNoTracking()
                .Where(x => x.Preferences != null)
                .Select(x => x.AuthentikSubjectId)
                .ToArrayAsync(cancellationToken);

            var subject = $"FrostStream schedule failed: {scheduleKey}";
            var body = string.IsNullOrWhiteSpace(failureMessage)
                ? $"Scheduled task '{scheduleKey}' failed."
                : $"Scheduled task '{scheduleKey}' failed: {failureMessage}";

            foreach (var user in users)
                await NotifyUserAsync(user, NotificationEventKeys.ScheduleFailed, subject, body, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed dispatching schedule failure notification for {ScheduleKey}.", scheduleKey);
        }
    }

    private async Task NotifyUserAsync(
        string ownerSubject,
        string eventKey,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(ownerSubject, cancellationToken);
        if (preferences is not { Enabled: true })
            return;

        var rule = preferences.Rules.FirstOrDefault(x =>
            x.Enabled && string.Equals(x.EventKey, eventKey, StringComparison.Ordinal));
        if (rule is null || rule.ProviderKeys.Count == 0)
            return;

        foreach (var providerKey in rule.ProviderKeys.Distinct(StringComparer.Ordinal))
        {
            var provider = preferences.Providers.FirstOrDefault(x =>
                x.Enabled && string.Equals(x.ProviderKey, providerKey, StringComparison.Ordinal));
            if (provider is null)
                continue;

            var result = await SendProviderAsync(ownerSubject, provider, eventKey, subject, body, cancellationToken);
            if (!result.Success)
            {
                logger.LogWarning(
                    "Notification provider {ProviderKey} failed for user {OwnerSubject} event {EventKey}: {ErrorCode} {ErrorMessage}",
                    provider.ProviderKey,
                    ownerSubject,
                    eventKey,
                    result.ErrorCode,
                    result.ErrorMessage);
            }
        }
    }

    private async Task<NotificationPreferencesDto?> LoadPreferencesAsync(
        string ownerSubject,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var user = await db.FrostStreamUsers
            .AsNoTracking()
            .Where(x => x.AuthentikSubjectId == ownerSubject)
            .FirstOrDefaultAsync(cancellationToken);

        return user is null ? null : NotificationPreferencesJson.Read(user.Preferences);
    }

    private async Task<NotificationDispatchResult> SendProviderAsync(
        string ownerSubject,
        NotificationProviderDto provider,
        string eventKey,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (!provider.Enabled)
            return new NotificationDispatchResult(false, "disabled", "Notification provider is disabled.");

        if (NotificationProfileValidator.Validate(provider) is { } validationError)
            return new NotificationDispatchResult(false, "validation", validationError);

        try
        {
            var expanded = await ExpandSecretsAsync(ownerSubject, provider.ProviderKey, provider.NotifyConfig, cancellationToken);
            var channelOptions = DeserializeChannelOptions(provider.ProviderKind, expanded);
            if (channelOptions is null)
                return new NotificationDispatchResult(false, "validation", $"Unsupported notification provider kind '{provider.ProviderKind}'.");

            await using var notifyProvider = BuildNotifyServiceProvider(provider.ProviderKind, channelOptions, eventKey);
            var notify = notifyProvider.GetRequiredService<INotifyService>();
            var channel = NormalizeChannel(provider.ProviderKind);
            var payload = new NotificationPayload
            {
                To = provider.DefaultTo ?? string.Empty,
                Subject = subject,
                Body = body,
                Metadata = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["froststream.providerKey"] = provider.ProviderKey,
                    ["froststream.eventKey"] = eventKey
                }
            };

            var result = await notify.TriggerAsync(
                eventKey,
                new NotifyContext
                {
                    User = new NotifyUser { UserId = ownerSubject },
                    Channels = new Dictionary<string, NotificationPayload>(StringComparer.OrdinalIgnoreCase)
                    {
                        [channel] = payload
                    },
                    Metadata = payload.Metadata
                },
                cancellationToken);

            return result.AnySucceeded
                ? new NotificationDispatchResult(true)
                : new NotificationDispatchResult(false, "send_failed", string.Join("; ", result.Failures.Select(x => x.Error)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notification send failed for user {OwnerSubject} provider {ProviderKey}.", ownerSubject, provider.ProviderKey);
            return new NotificationDispatchResult(false, "send_failed", ex.Message);
        }
    }

    private static object? DeserializeChannelOptions(string providerKind, JsonNode? expanded)
    {
        var property = typeof(NotifyOptions).GetProperty(ToNotifyOptionsProperty(providerKind));
        return property is null || expanded is null
            ? null
            : expanded.Deserialize(property.PropertyType, JsonOptions);
    }

    private static ServiceProvider BuildNotifyServiceProvider(string providerKind, object channelOptions, string eventKey)
    {
        var property = typeof(NotifyOptions).GetProperty(ToNotifyOptionsProperty(providerKind))
            ?? throw new InvalidOperationException($"Unsupported Notify provider kind '{providerKind}'.");
        var channel = NormalizeChannel(providerKind);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddRecurPixelNotify(
            options => property.SetValue(options, channelOptions),
            options => options.DefineEvent(eventKey, builder => builder.UseChannels([channel])));
        return services.BuildServiceProvider();
    }

    private async Task<JsonNode?> ExpandSecretsAsync(
        string ownerSubject,
        string providerKey,
        JsonElement element,
        CancellationToken cancellationToken)
    {
        var node = JsonNode.Parse(element.GetRawText());
        return await ExpandSecretsAsync(ownerSubject, providerKey, node, cancellationToken);
    }

    private async Task<JsonNode?> ExpandSecretsAsync(
        string ownerSubject,
        string providerKey,
        JsonNode? node,
        CancellationToken cancellationToken)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                    obj[property.Key] = await ExpandSecretsAsync(ownerSubject, providerKey, property.Value, cancellationToken);
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = await ExpandSecretsAsync(ownerSubject, providerKey, array[i], cancellationToken);
                return array;
            case JsonValue value:
                if (!value.TryGetValue<string>(out var str) ||
                    !str.StartsWith("secret://", StringComparison.Ordinal))
                {
                    return value;
                }

                if (!NotificationProfileValidator.TryParseSecretReference(str, out var referencedProvider, out var secretName) ||
                    !string.Equals(referencedProvider, providerKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Invalid notification secret reference '{str}'.");
                }

                var secret = await secretStore.ReadAsync(
                    SecretPaths.ForUserNotificationProvider(ownerSubject, referencedProvider),
                    cancellationToken);
                if (secret is null || !secret.TryGetValue(secretName, out var secretValue))
                    throw new InvalidOperationException($"Notification secret '{referencedProvider}/{secretName}' was not found.");

                return JsonValue.Create(secretValue);
            default:
                return node;
        }
    }

    private static string NormalizeChannel(string providerKind)
        => providerKind.Equals("rocketchat", StringComparison.OrdinalIgnoreCase)
            ? "rocketChat"
            : char.ToLowerInvariant(providerKind[0]) + providerKind[1..];

    private static string ToNotifyOptionsProperty(string providerKind)
        => providerKind.ToLowerInvariant() switch
        {
            "email" => nameof(NotifyOptions.Email),
            "sms" => nameof(NotifyOptions.Sms),
            "push" => nameof(NotifyOptions.Push),
            "whatsapp" => nameof(NotifyOptions.WhatsApp),
            "slack" => nameof(NotifyOptions.Slack),
            "discord" => nameof(NotifyOptions.Discord),
            "teams" => nameof(NotifyOptions.Teams),
            "telegram" => nameof(NotifyOptions.Telegram),
            "facebook" => nameof(NotifyOptions.Facebook),
            "line" => nameof(NotifyOptions.Line),
            "viber" => nameof(NotifyOptions.Viber),
            "mattermost" => nameof(NotifyOptions.Mattermost),
            "rocketchat" => nameof(NotifyOptions.RocketChat),
            _ => providerKind
        };
}
