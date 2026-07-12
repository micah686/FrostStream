using System.Text.Json;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Database;
using Shared.Downloads;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace DataBridge.Messaging;

public sealed class DownloadConfigSetConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadConfigSetConsumerService> logger) : SubscriptionBackgroundService
{
    private const string KeyPattern = "^[a-z0-9-]{2,100}$";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<DownloadConfigSetCreateRequestMessage>(messageBus, DownloadConfigSetSubjects.Create, HandleCreateAsync, DownloadConfigSetSubjects.ProcessorsQueueGroup, stoppingToken);
        await SubscribeAsync<DownloadConfigSetUpdateRequestMessage>(messageBus, DownloadConfigSetSubjects.Update, HandleUpdateAsync, DownloadConfigSetSubjects.ProcessorsQueueGroup, stoppingToken);
        await SubscribeAsync<DownloadConfigSetGetRequestMessage>(messageBus, DownloadConfigSetSubjects.Get, HandleGetAsync, DownloadConfigSetSubjects.ProcessorsQueueGroup, stoppingToken);
        await SubscribeAsync<DownloadConfigSetListRequestMessage>(messageBus, DownloadConfigSetSubjects.List, HandleListAsync, DownloadConfigSetSubjects.ProcessorsQueueGroup, stoppingToken);
        await SubscribeAsync<DownloadConfigSetDeleteRequestMessage>(messageBus, DownloadConfigSetSubjects.Delete, HandleDeleteAsync, DownloadConfigSetSubjects.ProcessorsQueueGroup, stoppingToken);
        await SubscribeAsync<DownloadConfigSetResolveRequestMessage>(messageBus, DownloadConfigSetSubjects.Resolve, HandleResolveAsync, DownloadConfigSetSubjects.ProcessorsQueueGroup, stoppingToken);
    }

    private async Task HandleCreateAsync(IMessageContext<DownloadConfigSetCreateRequestMessage> context)
    {
        var msg = context.Message;
        if (Validate(msg) is { } validation)
        {
            await context.RespondAsync(Failure("validation", validation));
            return;
        }

        try
        {
            var entity = ToEntity(msg);
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadConfigSetsRepository>();
            var created = await repo.CreateAsync(entity);
            await context.RespondAsync(new DownloadConfigSetOperationResponseMessage { Success = true, Entity = Map(created) });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Download config set create conflict for {OwnerSubject}/{Key}.", msg.OwnerSubject, msg.Key);
            await context.RespondAsync(Failure("conflict", $"Download config set '{msg.Key}' already exists."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating download config set {OwnerSubject}/{Key}.", msg.OwnerSubject, msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to create download config set."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<DownloadConfigSetUpdateRequestMessage> context)
    {
        var msg = context.Message;
        if (Validate(msg) is { } validation)
        {
            await context.RespondAsync(Failure("validation", validation));
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var updated = await scope.ServiceProvider.GetRequiredService<IDownloadConfigSetsRepository>()
                .UpdateAsync(ToEntity(msg));
            await context.RespondAsync(updated is null
                ? Failure("not_found", $"Download config set '{msg.Key}' was not found.")
                : new DownloadConfigSetOperationResponseMessage { Success = true, Entity = Map(updated) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating download config set {OwnerSubject}/{Key}.", msg.OwnerSubject, msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to update download config set."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<DownloadConfigSetGetRequestMessage> context)
    {
        var entity = await WithRepo(repo => repo.GetAsync(context.Message.OwnerSubject, context.Message.Key));
        await context.RespondAsync(entity is null
            ? Failure("not_found", $"Download config set '{context.Message.Key}' was not found.")
            : new DownloadConfigSetOperationResponseMessage { Success = true, Entity = Map(entity) });
    }

    private async Task HandleResolveAsync(IMessageContext<DownloadConfigSetResolveRequestMessage> context)
    {
        var entity = await WithRepo(repo => repo.GetAsync(context.Message.OwnerSubject, context.Message.Key));
        await context.RespondAsync(entity is null
            ? Failure("not_found", $"Download config set '{context.Message.Key}' was not found.")
            : new DownloadConfigSetOperationResponseMessage { Success = true, Entity = Map(entity) });
    }

    private async Task HandleListAsync(IMessageContext<DownloadConfigSetListRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListAsync(context.Message.OwnerSubject));
            await context.RespondAsync(new DownloadConfigSetOperationResponseMessage
            {
                Success = true,
                Items = items.Select(Map).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing download config sets for {OwnerSubject}.", context.Message.OwnerSubject);
            await context.RespondAsync(Failure("internal", "Failed to list download config sets."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<DownloadConfigSetDeleteRequestMessage> context)
    {
        var deleted = await WithRepo(repo => repo.DeleteAsync(context.Message.OwnerSubject, context.Message.Key));
        await context.RespondAsync(deleted
            ? new DownloadConfigSetOperationResponseMessage { Success = true }
            : Failure("not_found", $"Download config set '{context.Message.Key}' was not found."));
    }

    private Task<T> WithRepo<T>(Func<IDownloadConfigSetsRepository, Task<T>> action)
        => scopeFactory.WithScopedAsync(action);

    private static string? Validate(DownloadConfigSetCreateRequestMessage msg)
    {
        if (string.IsNullOrWhiteSpace(msg.OwnerSubject))
            return "ownerSubject is required.";
        if (string.IsNullOrWhiteSpace(msg.Key) || !System.Text.RegularExpressions.Regex.IsMatch(msg.Key, KeyPattern))
            return "key must match ^[a-z0-9-]{2,100}$.";
        if (string.IsNullOrWhiteSpace(msg.Name))
            return "name is required.";
        if (msg.Priority is < 0 or > 100)
            return "priority must be between 0 and 100.";
        if (!string.IsNullOrWhiteSpace(msg.YtDlpOptionsJson))
        {
            try
            {
                JsonSerializer.Deserialize<YtDlpOptions>(msg.YtDlpOptionsJson);
            }
            catch (JsonException)
            {
                return "ytdlpOptionsJson must be a valid YtDlpOptions document.";
            }
        }

        if (msg.IgnoreKeywords.Count > IgnoreKeywordMatcher.MaxKeywordCount)
            return $"ignoreKeywords must contain at most {IgnoreKeywordMatcher.MaxKeywordCount} entries.";
        foreach (var keyword in msg.IgnoreKeywords)
        {
            if (IgnoreKeywordMatcher.Validate(keyword) is { } keywordError)
                return keywordError;
        }

        return null;
    }

    private static DownloadConfigSetEntity ToEntity(DownloadConfigSetCreateRequestMessage msg)
        => new()
        {
            OwnerSubject = msg.OwnerSubject,
            Key = msg.Key,
            Name = msg.Name,
            Description = Normalize(msg.Description),
            StorageKey = Normalize(msg.StorageKey),
            CookieProfileKey = Normalize(msg.CookieProfileKey),
            YtDlpOptionsJson = Normalize(msg.YtDlpOptionsJson),
            IgnoreKeywordsJson = IgnoreKeywordMatcher.Serialize(msg.IgnoreKeywords),
            EncodeForPlaylist = msg.EncodeForPlaylist,
            AudioFormat = msg.AudioFormat,
            Priority = msg.Priority,
            FetchComments = msg.FetchComments
        };

    private static DownloadConfigSetDto Map(DownloadConfigSetEntity entity)
        => new()
        {
            Id = entity.Id,
            OwnerSubject = entity.OwnerSubject,
            Key = entity.Key,
            Name = entity.Name,
            Description = entity.Description,
            StorageKey = entity.StorageKey,
            CookieProfileKey = entity.CookieProfileKey,
            YtDlpOptionsJson = entity.YtDlpOptionsJson,
            IgnoreKeywords = IgnoreKeywordMatcher.Deserialize(entity.IgnoreKeywordsJson),
            EncodeForPlaylist = entity.EncodeForPlaylist,
            AudioFormat = entity.AudioFormat,
            Priority = entity.Priority,
            FetchComments = entity.FetchComments,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DownloadConfigSetOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

}
