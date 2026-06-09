using DataBridge;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
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
    ILogger<ScheduleCrudConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-schedules";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<ScheduleCreateRequestMessage>(messageBus, ScheduleSubjects.Create, HandleCreateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleUpdateRequestMessage>(messageBus, ScheduleSubjects.Update, HandleUpdateAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleGetRequestMessage>(messageBus, ScheduleSubjects.Get, HandleGetAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleListRequestMessage>(messageBus, ScheduleSubjects.List, HandleListAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleListActiveRequestMessage>(messageBus, ScheduleSubjects.ActiveList, HandleListActiveAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleListOverdueRequestMessage>(messageBus, ScheduleSubjects.Overdue, HandleListOverdueAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleDeleteRequestMessage>(messageBus, ScheduleSubjects.Delete, HandleDeleteAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleMarkAttemptRequestMessage>(messageBus, ScheduleSubjects.MarkAttempt, HandleMarkAttemptAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleMarkSuccessRequestMessage>(messageBus, ScheduleSubjects.MarkSuccess, HandleMarkSuccessAsync, QueueGroup, stoppingToken);
        await SubscribeAsync<ScheduleMarkFailureRequestMessage>(messageBus, ScheduleSubjects.MarkFailure, HandleMarkFailureAsync, QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to schedule CRUD subjects.");
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

            if (await WithRepo(repo => repo.GetByKeyAsync(msg.Key)) is not null)
            {
                await context.RespondAsync(Failure("conflict", $"Schedule key '{msg.Key}' already exists."));
                return;
            }

            var entity = await WithRepo(repo => repo.CreateAsync(ToEntity(msg)));
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

            var entity = await WithRepo(repo => repo.UpdateAsync(ToEntity(msg)));
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
            var entity = await WithRepo(repo => repo.GetByKeyAsync(key));
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
            var items = await WithRepo(list);
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
            var deleted = await WithRepo(repo => repo.DeleteAsync(key));
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
        => await WithRepo(repo => repo.MarkAttemptAsync(context.Message.Key, context.Message.AttemptedAt));

    private async Task HandleMarkSuccessAsync(IMessageContext<ScheduleMarkSuccessRequestMessage> context)
    {
        var entity = await WithRepo(repo => repo.MarkSuccessAsync(context.Message.Key, context.Message.SucceededAt));
        if (entity is not null)
        {
            await PublishChangedAsync(entity.Key, ScheduleChangeKind.Updated);
        }
    }

    private async Task HandleMarkFailureAsync(IMessageContext<ScheduleMarkFailureRequestMessage> context)
    {
        var entity = await WithRepo(repo => repo.MarkFailureAsync(context.Message.Key, context.Message.FailedAt));
        if (entity is not null)
        {
            await PublishChangedAsync(entity.Key, ScheduleChangeKind.Updated);
        }
    }

    private Task<TResult> WithRepo<TResult>(Func<IScheduledTasksRepository, Task<TResult>> action)
        => scopeFactory.WithScopedAsync(action);

    private Task WithRepo(Func<IScheduledTasksRepository, Task> action)
        => scopeFactory.WithScopedAsync(action);

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
        LastRunStatus = entity.LastRunStatus,
        NextDueAt = entity.NextDueAt,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated
    };
}
