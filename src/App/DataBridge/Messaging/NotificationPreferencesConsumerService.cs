using System.Text.Json;
using System.Text.Json.Nodes;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class NotificationPreferencesConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    INotificationDispatcher dispatcher,
    IClock clock,
    ILogger<NotificationPreferencesConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-notifications";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<NotificationGetPreferencesRequestMessage>(messageBus, NotificationSubjects.GetPreferences, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<NotificationUpdatePreferencesRequestMessage>(messageBus, NotificationSubjects.UpdatePreferences, HandleUpdateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<NotificationUpsertProviderRequestMessage>(messageBus, NotificationSubjects.UpsertProvider, HandleUpsertProviderAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<NotificationDeleteProviderRequestMessage>(messageBus, NotificationSubjects.DeleteProvider, HandleDeleteProviderAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<NotificationTestRequestMessage>(messageBus, NotificationSubjects.Test, HandleTestAsync, QueueGroup, stoppingToken);
        logger.LogInformation("Subscribed to notification preference subjects.");
    }

    private async Task HandleGetAsync(IMessageContext<NotificationGetPreferencesRequestMessage> context)
    {
        try
        {
            var preferences = await WithDb(db => ReadPreferencesAsync(db, context.Message.OwnerSubject));
            await context.RespondAsync(preferences is null
                ? Failure("not_found", "User was not found.")
                : new NotificationOperationResponseMessage { Success = true, Preferences = preferences });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting notification preferences for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal_error", "Internal notification preference service error."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<NotificationUpdatePreferencesRequestMessage> context)
    {
        try
        {
            if (NotificationProfileValidator.Validate(context.Message.Preferences) is { } error)
            {
                await context.RespondAsync(Failure("validation", error));
                return;
            }

            var result = await WithDb(db => WritePreferencesAsync(db, context.Message.OwnerSubject, context.Message.Preferences));
            await context.RespondAsync(result is null
                ? Failure("not_found", "User was not found.")
                : new NotificationOperationResponseMessage { Success = true, Preferences = result });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating notification preferences for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal_error", "Internal notification preference service error."));
        }
    }

    private async Task HandleUpsertProviderAsync(IMessageContext<NotificationUpsertProviderRequestMessage> context)
    {
        try
        {
            if (NotificationProfileValidator.Validate(context.Message.Provider) is { } error)
            {
                await context.RespondAsync(Failure("validation", error));
                return;
            }

            var result = await WithDb(db => UpsertProviderAsync(db, context.Message.OwnerSubject, context.Message.Provider));
            await context.RespondAsync(result is null
                ? Failure("not_found", "User was not found.")
                : new NotificationOperationResponseMessage { Success = true, Preferences = result, Provider = context.Message.Provider });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed upserting notification provider for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal_error", "Internal notification provider service error."));
        }
    }

    private async Task HandleDeleteProviderAsync(IMessageContext<NotificationDeleteProviderRequestMessage> context)
    {
        try
        {
            var result = await WithDb(db => DeleteProviderAsync(db, context.Message.OwnerSubject, context.Message.ProviderKey));
            await context.RespondAsync(result is null
                ? Failure("not_found", "User was not found.")
                : new NotificationOperationResponseMessage { Success = true, Preferences = result });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting notification provider {ProviderKey} for {OwnerSubject}.",
                context.Message.ProviderKey,
                context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal_error", "Internal notification provider service error."));
        }
    }

    private async Task HandleTestAsync(IMessageContext<NotificationTestRequestMessage> context)
    {
        try
        {
            var result = await dispatcher.SendTestAsync(context.Message, CancellationToken.None);
            await context.RespondAsync(result.Success
                ? new NotificationOperationResponseMessage { Success = true }
                : Failure(result.ErrorCode ?? "send_failed", result.ErrorMessage ?? "Notification test failed."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed sending notification test for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal_error", "Internal notification test service error."));
        }
    }

    private Task<TResult> WithDb<TResult>(Func<DataBridgeDbContext, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private static async Task<NotificationPreferencesDto?> ReadPreferencesAsync(DataBridgeDbContext db, string ownerSubject)
    {
        var user = await db.FrostStreamUsers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AuthentikSubjectId == ownerSubject);
        return user is null ? null : NotificationPreferencesJson.Read(user.Preferences);
    }

    private async Task<NotificationPreferencesDto?> WritePreferencesAsync(
        DataBridgeDbContext db,
        string ownerSubject,
        NotificationPreferencesDto preferences)
    {
        var user = await db.FrostStreamUsers.FirstOrDefaultAsync(x => x.AuthentikSubjectId == ownerSubject);
        if (user is null)
            return null;

        user.Preferences = NotificationPreferencesJson.Write(user.Preferences, preferences);
        user.LastUpdated = clock.GetCurrentInstant();
        await db.SaveChangesAsync();
        return preferences;
    }

    private async Task<NotificationPreferencesDto?> UpsertProviderAsync(
        DataBridgeDbContext db,
        string ownerSubject,
        NotificationProviderDto provider)
    {
        var current = await ReadPreferencesAsync(db, ownerSubject);
        if (current is null)
            return null;

        var providers = current.Providers
            .Where(x => !string.Equals(x.ProviderKey, provider.ProviderKey, StringComparison.Ordinal))
            .Append(provider)
            .OrderBy(x => x.ProviderKey, StringComparer.Ordinal)
            .ToArray();

        var next = current with { Providers = providers };
        return await WritePreferencesAsync(db, ownerSubject, next);
    }

    private async Task<NotificationPreferencesDto?> DeleteProviderAsync(
        DataBridgeDbContext db,
        string ownerSubject,
        string providerKey)
    {
        var current = await ReadPreferencesAsync(db, ownerSubject);
        if (current is null)
            return null;

        var providers = current.Providers
            .Where(x => !string.Equals(x.ProviderKey, providerKey, StringComparison.Ordinal))
            .ToArray();
        var rules = current.Rules
            .Select(rule => rule with
            {
                ProviderKeys = rule.ProviderKeys
                    .Where(key => !string.Equals(key, providerKey, StringComparison.Ordinal))
                    .ToArray()
            })
            .ToArray();

        var next = current with { Providers = providers, Rules = rules };
        return await WritePreferencesAsync(db, ownerSubject, next);
    }

    private static NotificationOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

internal static class NotificationPreferencesJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static NotificationPreferencesDto Read(string? preferencesJson)
    {
        if (string.IsNullOrWhiteSpace(preferencesJson))
            return new NotificationPreferencesDto();

        var root = JsonNode.Parse(preferencesJson)?.AsObject();
        if (root is null || root["notifications"] is not { } notifications)
            return new NotificationPreferencesDto();

        return notifications.Deserialize<NotificationPreferencesDto>(JsonOptions) ?? new NotificationPreferencesDto();
    }

    public static string Write(string? preferencesJson, NotificationPreferencesDto preferences)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(preferencesJson))
        {
            root = [];
        }
        else
        {
            root = JsonNode.Parse(preferencesJson)?.AsObject() ?? [];
        }

        root["notifications"] = JsonSerializer.SerializeToNode(preferences, JsonOptions);
        return root.ToJsonString(JsonOptions);
    }
}
