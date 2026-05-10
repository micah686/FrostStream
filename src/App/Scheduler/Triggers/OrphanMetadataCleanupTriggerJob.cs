using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using NodaTime;
using Quartz;
using Shared.Messaging;

namespace Scheduler.Triggers;

/// <summary>
/// Thin Quartz job: read schedule context from <see cref="IJobExecutionContext"/>,
/// build the <see cref="OrphanMetadataCleanupRequested"/> command with a deterministic
/// idempotency key, publish to JetStream. No DB access, no retry loop — JetStream
/// owns retry, DataBridge owns DB state.
/// </summary>
[DisallowConcurrentExecution]
public sealed class OrphanMetadataCleanupTriggerJob(
    IJetStreamPublisher publisher,
    IClock clock,
    ILogger<OrphanMetadataCleanupTriggerJob> logger) : IJob
{
    public const string ScheduleKeyDataKey = "scheduleKey";

    public async Task Execute(IJobExecutionContext context)
    {
        var scheduleKey = context.MergedJobDataMap.GetString(ScheduleKeyDataKey);
        if (string.IsNullOrWhiteSpace(scheduleKey))
            throw new InvalidOperationException(
                $"Quartz job {nameof(OrphanMetadataCleanupTriggerJob)} fired without {ScheduleKeyDataKey} in JobDataMap.");

        // Use the scheduled fire-time (not 'now') as the dedupe window, so a Quartz
        // misfire-replay or Scheduler restart that re-publishes for the same window
        // hits stream-level dedupe instead of double-cleaning.
        var window = context.ScheduledFireTimeUtc ?? context.FireTimeUtc;
        var idempotencyKey = BuildIdempotencyKey(scheduleKey, window);

        var msg = new OrphanMetadataCleanupRequested
        {
            ScheduleKey = scheduleKey,
            CorrelationId = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            TriggeredAt = clock.GetCurrentInstant()
        };

        logger.LogInformation(
            "Publishing OrphanMetadataCleanupRequested ScheduleKey {ScheduleKey} IdempotencyKey {IdempotencyKey}",
            scheduleKey, idempotencyKey);

        await publisher.PublishAsync(
            ScheduleSubjects.OrphanMetadataCleanupRequest,
            msg,
            messageId: idempotencyKey,
            cancellationToken: context.CancellationToken);
    }

    public static string BuildIdempotencyKey(string scheduleKey, DateTimeOffset window)
        => $"{TaskTypeRegistry.OrphanMetadataCleanup}:{scheduleKey}:{window.UtcDateTime:O}";
}
