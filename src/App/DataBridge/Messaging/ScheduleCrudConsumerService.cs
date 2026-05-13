using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Quartz;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class ScheduleCrudConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ScheduleCrudConsumerService> logger) : BackgroundService
{
    private const string QueueGroup = "databridge-schedules";
    private readonly List<ISubscription> _subscriptions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleCreateRequestMessage>(ScheduleSubjects.Create, HandleCreateAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleUpdateRequestMessage>(ScheduleSubjects.Update, HandleUpdateAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleGetRequestMessage>(ScheduleSubjects.Get, HandleGetAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleListRequestMessage>(ScheduleSubjects.List, HandleListAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleListActiveRequestMessage>(ScheduleSubjects.ActiveList, HandleListActiveAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleListOverdueRequestMessage>(ScheduleSubjects.Overdue, HandleListOverdueAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleDeleteRequestMessage>(ScheduleSubjects.Delete, HandleDeleteAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleMarkAttemptRequestMessage>(ScheduleSubjects.MarkAttempt, HandleMarkAttemptAsync, QueueGroup, stoppingToken));
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleMarkSuccessRequestMessage>(ScheduleSubjects.MarkSuccess, HandleMarkSuccessAsync, QueueGroup, stoppingToken));

        logger.LogInformation("Subscribed to schedule CRUD subjects.");

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

    private async Task HandleCreateAsync(IMessageContext<ScheduleCreateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg) is { } validationError)
            {
                await context.RespondAsync(Failure("validation", validationError));
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            if (await repo.GetByKeyAsync(msg.Key) is not null)
            {
                await context.RespondAsync(Failure("conflict", $"Schedule key '{msg.Key}' already exists."));
                return;
            }

            var entity = await repo.CreateAsync(ToEntity(msg));
            await PublishChangedAsync(entity.Key, ScheduleChangeKind.Created);
            await context.RespondAsync(Success(entity));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed creating schedule '{Key}'", msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to create schedule."));
        }
    }

    private async Task HandleUpdateAsync(IMessageContext<ScheduleUpdateRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            if (Validate(msg) is { } validationError)
            {
                await context.RespondAsync(Failure("validation", validationError));
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var entity = await repo.UpdateAsync(ToEntity(msg));
            if (entity is null)
            {
                await context.RespondAsync(Failure("not_found", $"Schedule key '{msg.Key}' was not found."));
                return;
            }

            await PublishChangedAsync(entity.Key, ScheduleChangeKind.Updated);
            await context.RespondAsync(Success(entity));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed updating schedule '{Key}'", msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to update schedule."));
        }
    }

    private async Task HandleGetAsync(IMessageContext<ScheduleGetRequestMessage> context)
    {
        var key = context.Message.Key;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var entity = await repo.GetByKeyAsync(key);
            await context.RespondAsync(entity is null ? Failure("not_found", $"Schedule key '{key}' was not found.") : Success(entity));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting schedule '{Key}'", key);
            await context.RespondAsync(Failure("internal", "Failed to get schedule."));
        }
    }

    private Task HandleListAsync(IMessageContext<ScheduleListRequestMessage> context)
        => RespondWithListAsync(context, repo => repo.ListAsync());

    private Task HandleListActiveAsync(IMessageContext<ScheduleListActiveRequestMessage> context)
        => RespondWithListAsync(context, repo => repo.ListActiveAsync());

    private Task HandleListOverdueAsync(IMessageContext<ScheduleListOverdueRequestMessage> context)
        => RespondWithListAsync(context, repo => repo.ListOverdueAsync());

    private async Task RespondWithListAsync<TRequest>(
        IMessageContext<TRequest> context,
        Func<IScheduledTasksRepository, Task<IReadOnlyList<ScheduledTaskEntity>>> list)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var items = await list(repo);
            await context.RespondAsync(new ScheduleOperationResponseMessage
            {
                Success = true,
                Items = items.Select(Map).ToArray()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed listing schedules.");
            await context.RespondAsync(Failure("internal", "Failed to list schedules."));
        }
    }

    private async Task HandleDeleteAsync(IMessageContext<ScheduleDeleteRequestMessage> context)
    {
        var key = context.Message.Key;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var deleted = await repo.DeleteAsync(key);
            if (!deleted)
            {
                await context.RespondAsync(Failure("not_found", $"Schedule key '{key}' was not found."));
                return;
            }

            await PublishChangedAsync(key, ScheduleChangeKind.Deleted);
            await context.RespondAsync(new ScheduleOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting schedule '{Key}'", key);
            await context.RespondAsync(Failure("internal", "Failed to delete schedule."));
        }
    }

    private async Task HandleMarkAttemptAsync(IMessageContext<ScheduleMarkAttemptRequestMessage> context)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
        await repo.MarkAttemptAsync(context.Message.Key, context.Message.AttemptedAt);
    }

    private async Task HandleMarkSuccessAsync(IMessageContext<ScheduleMarkSuccessRequestMessage> context)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
        var entity = await repo.MarkSuccessAsync(context.Message.Key, context.Message.SucceededAt);
        if (entity is not null)
        {
            await PublishChangedAsync(entity.Key, ScheduleChangeKind.Updated);
        }
    }

    private async Task PublishChangedAsync(string key, ScheduleChangeKind kind)
        => await messageBus.PublishAsync(ScheduleSubjects.Changed, new ScheduleChangedMessage
        {
            Key = key,
            Kind = kind,
            OccurredAt = clock.GetCurrentInstant()
        });

    private static string? Validate(ScheduleCreateRequestMessage msg)
        => ValidateCore(msg.Key, msg.TaskType, msg.Cron, msg.IntervalSeconds, msg.Timezone);

    private static string? Validate(ScheduleUpdateRequestMessage msg)
        => ValidateCore(msg.Key, msg.TaskType, msg.Cron, msg.IntervalSeconds, msg.Timezone);

    private static string? ValidateCore(string key, string taskType, string? cron, int? intervalSeconds, string timezone)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "key is required.";
        if (string.IsNullOrWhiteSpace(taskType))
            return "task_type is required.";
        var hasCron = !string.IsNullOrWhiteSpace(cron);
        var hasInterval = intervalSeconds is not null;
        if (hasCron == hasInterval)
            return "Exactly one of cron or interval_seconds must be supplied.";
        if (intervalSeconds is <= 0)
            return "interval_seconds must be greater than zero.";
        if (!string.IsNullOrWhiteSpace(cron) && !CronExpression.IsValidExpression(cron))
            return "cron is not a valid Quartz cron expression.";
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(timezone) is null)
            return "timezone is not a valid TZDB timezone id.";
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return "timezone is not available on this host.";
        }

        return null;
    }

    private static ScheduledTaskEntity ToEntity(ScheduleCreateRequestMessage msg)
        => new()
        {
            Key = msg.Key,
            TaskType = msg.TaskType,
            Cron = string.IsNullOrWhiteSpace(msg.Cron) ? null : msg.Cron,
            IntervalSeconds = msg.IntervalSeconds,
            Timezone = msg.Timezone,
            Enabled = msg.Enabled,
            CatchupPolicy = msg.CatchupPolicy
        };

    private static ScheduledTaskEntity ToEntity(ScheduleUpdateRequestMessage msg)
        => new()
        {
            Key = msg.Key,
            TaskType = msg.TaskType,
            Cron = string.IsNullOrWhiteSpace(msg.Cron) ? null : msg.Cron,
            IntervalSeconds = msg.IntervalSeconds,
            Timezone = msg.Timezone,
            Enabled = msg.Enabled,
            CatchupPolicy = msg.CatchupPolicy
        };

    private static ScheduleOperationResponseMessage Success(ScheduledTaskEntity entity)
        => new() { Success = true, Entity = Map(entity) };

    private static ScheduleOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };

    private static ScheduledTaskDto Map(ScheduledTaskEntity entity) => new()
    {
        Id = entity.Id,
        Key = entity.Key,
        TaskType = entity.TaskType,
        Cron = entity.Cron,
        IntervalSeconds = entity.IntervalSeconds,
        Timezone = entity.Timezone,
        Enabled = entity.Enabled,
        CatchupPolicy = entity.CatchupPolicy,
        LastAttemptAt = entity.LastAttemptAt,
        LastSuccessAt = entity.LastSuccessAt,
        NextDueAt = entity.NextDueAt,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated
    };
}
