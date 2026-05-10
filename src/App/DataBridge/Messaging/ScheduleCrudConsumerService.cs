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

/// <summary>
/// Owns the <c>scheduled_tasks</c> table from a NATS perspective. Mirrors
/// <see cref="OptionPresetCrudConsumerService"/>: subscribe to request/reply subjects
/// in <see cref="ScheduleSubjects"/>, validate, mutate, respond with
/// <see cref="ScheduleOperationResponseMessage"/>. Broadcasts
/// <see cref="ScheduleSubjects.ScheduleChanged"/> on every successful mutation so
/// the Scheduler service can refresh its in-memory Quartz triggers.
/// </summary>
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
        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleCreateRequestMessage>(
            ScheduleSubjects.CreateSchedule, HandleCreateAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleUpdateRequestMessage>(
            ScheduleSubjects.UpdateSchedule, HandleUpdateAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleGetRequestMessage>(
            ScheduleSubjects.GetSchedule, HandleGetAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleListRequestMessage>(
            ScheduleSubjects.ListSchedules, HandleListAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleDeleteRequestMessage>(
            ScheduleSubjects.DeleteSchedule, HandleDeleteAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleListActiveRequestMessage>(
            ScheduleSubjects.ListActive, HandleListActiveAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleListOverdueRequestMessage>(
            ScheduleSubjects.ListOverdue, HandleListOverdueAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleMarkAttemptRequestMessage>(
            ScheduleSubjects.MarkAttempt, HandleMarkAttemptAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

        _subscriptions.Add(await messageBus.SubscribeAsync<ScheduleMarkSuccessRequestMessage>(
            ScheduleSubjects.MarkSuccess, HandleMarkSuccessAsync, queueGroup: QueueGroup, cancellationToken: stoppingToken));

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
            if (Validate(msg.Cron, msg.IntervalSeconds, msg.Timezone) is { } error)
            {
                await context.RespondAsync(Failure("validation", error));
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();

            if (await repo.GetByKeyAsync(msg.Key) is not null)
            {
                await context.RespondAsync(Failure("conflict", $"Schedule key '{msg.Key}' already exists."));
                return;
            }

            var entity = await repo.CreateAsync(new ScheduledTaskEntity
            {
                Key = msg.Key,
                TaskType = msg.TaskType,
                Cron = msg.Cron,
                IntervalSeconds = msg.IntervalSeconds,
                Timezone = msg.Timezone,
                Enabled = msg.Enabled,
                CatchupPolicy = (ScheduleCatchupPolicy)msg.CatchupPolicy
            });

            await context.RespondAsync(new ScheduleOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });

            await BroadcastAsync(msg.Key, ScheduleChangeKind.Created);
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
            if (Validate(msg.Cron, msg.IntervalSeconds, msg.Timezone) is { } error)
            {
                await context.RespondAsync(Failure("validation", error));
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();

            var updated = await repo.UpdateAsync(new ScheduledTaskEntity
            {
                Key = msg.Key,
                TaskType = msg.TaskType,
                Cron = msg.Cron,
                IntervalSeconds = msg.IntervalSeconds,
                Timezone = msg.Timezone,
                Enabled = msg.Enabled,
                CatchupPolicy = (ScheduleCatchupPolicy)msg.CatchupPolicy
            });

            if (updated is null)
            {
                await context.RespondAsync(Failure("not_found", $"Schedule key '{msg.Key}' was not found."));
                return;
            }

            await context.RespondAsync(new ScheduleOperationResponseMessage
            {
                Success = true,
                Entity = Map(updated)
            });

            await BroadcastAsync(msg.Key, ScheduleChangeKind.Updated);
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
            if (entity is null)
            {
                await context.RespondAsync(Failure("not_found", $"Schedule key '{key}' was not found."));
                return;
            }

            await context.RespondAsync(new ScheduleOperationResponseMessage
            {
                Success = true,
                Entity = Map(entity)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed getting schedule '{Key}'", key);
            await context.RespondAsync(Failure("internal", "Failed to get schedule."));
        }
    }

    private Task HandleListAsync(IMessageContext<ScheduleListRequestMessage> context)
        => RespondListAsync(context, async repo => await repo.ListAsync());

    private Task HandleListActiveAsync(IMessageContext<ScheduleListActiveRequestMessage> context)
        => RespondListAsync(context, async repo => await repo.ListActiveAsync());

    private Task HandleListOverdueAsync(IMessageContext<ScheduleListOverdueRequestMessage> context)
        => RespondListAsync(context, async repo => await repo.ListOverdueAsync(clock.GetCurrentInstant()));

    private async Task RespondListAsync<T>(
        IMessageContext<T> context,
        Func<IScheduledTasksRepository, Task<IReadOnlyList<ScheduledTaskEntity>>> fetch) where T : class
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var items = await fetch(repo);
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

            await context.RespondAsync(new ScheduleOperationResponseMessage { Success = true });
            await BroadcastAsync(key, ScheduleChangeKind.Deleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed deleting schedule '{Key}'", key);
            await context.RespondAsync(Failure("internal", "Failed to delete schedule."));
        }
    }

    private async Task HandleMarkAttemptAsync(IMessageContext<ScheduleMarkAttemptRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var updated = await repo.MarkAttemptAsync(msg.Key, msg.AttemptedAt);
            if (updated is null)
            {
                await context.RespondAsync(Failure("not_found", $"Schedule key '{msg.Key}' was not found."));
                return;
            }
            await context.RespondAsync(new ScheduleOperationResponseMessage { Success = true, Entity = Map(updated) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking attempt for schedule '{Key}'", msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to mark attempt."));
        }
    }

    private async Task HandleMarkSuccessAsync(IMessageContext<ScheduleMarkSuccessRequestMessage> context)
    {
        var msg = context.Message;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
            var result = await repo.MarkSuccessAsync(msg.Key, msg.SucceededAt);
            if (result is null)
            {
                await context.RespondAsync(Failure("not_found", $"Schedule key '{msg.Key}' was not found."));
                return;
            }

            logger.LogInformation(
                "Schedule '{Key}' success recorded; idempotencyKey={IdempotencyKey} previousLastSuccessAt={Previous} nextDueAt={NextDueAt}",
                msg.Key, msg.IdempotencyKey, result.PreviousLastSuccessAt, result.Entity.NextDueAt);

            await context.RespondAsync(new ScheduleOperationResponseMessage { Success = true, Entity = Map(result.Entity) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed marking success for schedule '{Key}'", msg.Key);
            await context.RespondAsync(Failure("internal", "Failed to mark success."));
        }
    }

    private async Task BroadcastAsync(string key, ScheduleChangeKind change)
    {
        try
        {
            await messageBus.PublishAsync(
                ScheduleSubjects.ScheduleChanged,
                new ScheduleChangedMessage { Key = key, Change = change });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed broadcasting schedule change for '{Key}' ({Change})", key, change);
        }
    }

    /// <summary>Returns null when valid, an error message string otherwise.</summary>
    private static string? Validate(string? cron, int? intervalSeconds, string timezone)
    {
        var hasCron = !string.IsNullOrWhiteSpace(cron);
        var hasInterval = intervalSeconds is > 0;
        if (hasCron == hasInterval)
            return "Exactly one of cron or intervalSeconds must be set.";

        if (hasCron)
        {
            try
            {
                _ = new CronExpression(cron!);
            }
            catch (FormatException ex)
            {
                return $"Invalid cron expression: {ex.Message}";
            }
        }

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                return $"Unknown timezone: '{timezone}'.";
            }
        }

        return null;
    }

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
        CatchupPolicy = (ScheduleCatchupPolicyDto)entity.CatchupPolicy,
        LastAttemptAt = entity.LastAttemptAt,
        LastSuccessAt = entity.LastSuccessAt,
        NextDueAt = entity.NextDueAt,
        CreatedAt = entity.CreatedAt,
        LastUpdated = entity.LastUpdated
    };
}
