using System.Text.Json;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    ILogger<OptionPresetCrudConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "databridge-option-presets";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<OptionPresetCreateRequestMessage>(
            OptionPresetSubjects.CreatePreset,
            HandleCreateAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<OptionPresetUpdateRequestMessage>(
            OptionPresetSubjects.UpdatePreset,
            HandleUpdateAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<OptionPresetGetRequestMessage>(
            OptionPresetSubjects.GetPreset,
            HandleGetAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<OptionPresetListRequestMessage>(
            OptionPresetSubjects.ListPresets,
            HandleListAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<OptionPresetDeleteRequestMessage>(
            OptionPresetSubjects.DeletePreset,
            HandleDeleteAsync,
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken));

        logger.LogInformation("Subscribed to option-preset CRUD subjects.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.StopAsync(cancellationToken);
            await subscription.DisposeAsync();
        }

        _subscriptions.Clear();
        await base.StopAsync(cancellationToken);
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

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOptionPresetsRepository>();

            if (await repo.GetByKeyAsync(msg.Key) is not null)
            {
                await context.RespondAsync(Failure("conflict", $"Preset key '{msg.Key}' already exists."));
                return;
            }

            var entity = await repo.CreateAsync(msg.Key, msg.Name, msg.Description, msg.YtDlpOptionsJson);
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

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOptionPresetsRepository>();

            var updated = await repo.UpdateAsync(msg.Key, msg.Name, msg.Description, msg.YtDlpOptionsJson);
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
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOptionPresetsRepository>();
            var entity = await repo.GetByKeyAsync(key);
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
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOptionPresetsRepository>();
            var items = await repo.ListAsync();
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
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IOptionPresetsRepository>();
            var deleted = await repo.DeleteAsync(key);
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
