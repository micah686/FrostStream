using System.Text.Json;
using DataBridge;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Database;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace DataBridge.Messaging;

/// <summary>
/// CRUD consumer for download option presets. Mirrors the request/reply pattern in
/// <see cref="StorageCrudConsumerService"/>.
/// </summary>
public sealed class OptionPresetCrudConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<OptionPresetCrudConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-option-presets";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<OptionPresetCreateRequestMessage>(
            messageBus,
            OptionPresetSubjects.CreatePreset,
            HandleCreateAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<OptionPresetUpdateRequestMessage>(
            messageBus,
            OptionPresetSubjects.UpdatePreset,
            HandleUpdateAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<OptionPresetGetRequestMessage>(
            messageBus,
            OptionPresetSubjects.GetPreset,
            HandleGetAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<OptionPresetListRequestMessage>(
            messageBus,
            OptionPresetSubjects.ListPresets,
            HandleListAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        await SubscribeAsync<OptionPresetDeleteRequestMessage>(
            messageBus,
            OptionPresetSubjects.DeletePreset,
            HandleDeleteAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        logger.LogInformation("Subscribed to option-preset CRUD subjects.");
    }

    private async Task HandleCreateAsync(IMessageContext<OptionPresetCreateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (ValidateOptionsJson(msg.YtDlpOptionsJson) is { } parseError)
            {
                await context.RespondAsync(Failure("validation", parseError));
                return;
            }

            if (await WithRepo(repo => repo.GetByKeyAsync(msg.Key)) is not null)
            {
                await context.RespondAsync(Failure("conflict", $"Preset key '{msg.Key}' already exists."));
                return;
            }

            var entity = await WithRepo(repo => repo.CreateAsync(msg.Key, msg.Name, msg.Description, msg.YtDlpOptionsJson));
            await context.RespondAsync(new OptionPresetOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating option preset '{Key}'", msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to create preset."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<OptionPresetUpdateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (ValidateOptionsJson(msg.YtDlpOptionsJson) is { } parseError)
            {
                await context.RespondAsync(Failure("validation", parseError));
                return;
            }

            var updated = await WithRepo(repo => repo.UpdateAsync(msg.Key, msg.Name, msg.Description, msg.YtDlpOptionsJson));
            if (updated is null)
            {
                await context.RespondAsync(Failure("not_found", $"Preset key '{msg.Key}' was not found."));
                return;
            }

            await context.RespondAsync(new OptionPresetOperationResponseMessage
            {
                Success = true,
                Entity = Map(updated)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating option preset '{Key}'", msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to update preset."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<OptionPresetGetRequestMessage> context)
    {
        var key = context.Message.Key;
        try
        {
            var entity = await WithRepo(repo => repo.GetByKeyAsync(key));
            if (entity is null)
            {
                await context.RespondAsync(Failure("not_found", $"Preset key '{key}' was not found."));
                return;
            }

            await context.RespondAsync(new OptionPresetOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting option preset '{Key}'", key);
            await context.RespondAsync(Failure("internal", "Failed to get preset."));
        }
    }

    private async Task HandleListAsync(IMessageContext<OptionPresetListRequestMessage> context)
    {
        try
        {
            var items = await WithRepo(repo => repo.ListAsync());
            await context.RespondAsync(new OptionPresetOperationResponseMessage
            {
                Success = true,
                Items = items.Select(Map).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing option presets.");
            await context.RespondAsync(Failure("internal", "Failed to list presets."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<OptionPresetDeleteRequestMessage> context)
    {
        var key = context.Message.Key;
        try
        {
            var deleted = await WithRepo(repo => repo.DeleteAsync(key));
            if (!deleted)
            {
                await context.RespondAsync(Failure("not_found", $"Preset key '{key}' was not found."));
                return;
            }

            await context.RespondAsync(new OptionPresetOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting option preset '{Key}'", key);
            await context.RespondAsync(Failure("internal", "Failed to delete preset."));
        }
    }

    private static string? ValidateOptionsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "ytdlp_options_json is required.";

        try
        {
            var options = JsonSerializer.Deserialize<YtDlpOptions>(json);
            if (options is null)
                return "ytdlp_options_json could not be parsed.";
            return null;
        }
        catch (JsonException ex)
        {
            return $"ytdlp_options_json is not valid: {ex.Message}";
        }
    }

    private Task<TResult> WithRepo<TResult>(Func<IOptionPresetsRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private static OptionPresetOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static OptionPresetDto Map(OptionPresetEntity entity) => new()
    {
        Id = entity.Id,
        Key = entity.Key,
        Name = entity.Name,
        Description = entity.Description,
        YtDlpOptionsJson = entity.YtDlpOptionsJson,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated
    };
}
