using Conduit.NATS;
using NodaTime;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Backups;
using Shared.Messaging;

namespace Scheduler.MaintenanceTasks;

/// <summary>
/// Dispatches scheduled backups to BackupService over REST (commands are infrequent, so no
/// JetStream queue) and polls the job until it settles. The Scheduler owns the schedule marks
/// and the admin failure notification; BackupService owns the Jobs &gt; Background run row,
/// where the manual and scheduled paths converge.
/// </summary>
public sealed class BackupScheduler(
    IBackupServiceClient client,
    IMessageBus messageBus,
    IClock clock,
    ILogger<BackupScheduler> logger) : IBackupScheduler
{
    /// <summary>Init-settable for tests.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan CompletionTimeout { get; init; } = TimeSpan.FromHours(2);

    public async Task QueueBackupAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        await messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
        {
            Key = context.ScheduleKey,
            AttemptedAt = clock.GetCurrentInstant()
        }, cancellationToken: cancellationToken);

        try
        {
            var type = context.TaskType.ToLowerInvariant() switch
            {
                TaskTypeRegistry.BackupFull => "full",
                TaskTypeRegistry.BackupDiff => "diff",
                _ => throw new InvalidOperationException($"Unsupported scheduled backup task type '{context.TaskType}'.")
            };
            var name = $"scheduled-{context.ScheduleKey}-{context.DueWindowUtc.ToDateTimeUtc():yyyyMMddHHmmss}";
            var job = await client.CreateAsync(new CreateBackupJobRequest(
                name,
                type,
                Scheduled: true,
                ScheduleKey: context.ScheduleKey,
                IdempotencyKey: context.IdempotencyKey), cancellationToken);

            var completed = await PollUntilTerminalAsync(job.JobId, cancellationToken);
            if (completed.Status != "completed")
                throw new InvalidOperationException(completed.ErrorMessage ?? "Scheduled backup failed.");

            await messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
            {
                Key = context.ScheduleKey,
                SucceededAt = clock.GetCurrentInstant()
            }, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The next schedule firing is the retry; mark the failure and notify rather than
            // failing the Quartz job.
            logger.LogError(ex, "Scheduled backup {IdempotencyKey} failed.", context.IdempotencyKey);
            await messageBus.PublishAsync(NotificationSubjects.DispatchAdmin, new NotificationDispatchAdminEventMessage
            {
                EventKey = NotificationEventKeys.BackupFailed,
                Subject = "FrostStream backup failed",
                Body = $"Scheduled backup '{context.ScheduleKey}' failed: {ex.Message}"
            }, cancellationToken: CancellationToken.None);
            await messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
            {
                Key = context.ScheduleKey,
                FailedAt = clock.GetCurrentInstant()
            }, cancellationToken: CancellationToken.None);
        }
    }

    internal async Task<BackupJobDto> PollUntilTerminalAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CompletionTimeout;
        while (true)
        {
            var job = await client.GetJobAsync(jobId, cancellationToken)
                      ?? throw new InvalidOperationException($"Backup job {jobId} disappeared from the backup service.");
            if (job.Status is "completed" or "failed")
                return job;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Backup job {jobId} did not finish within {CompletionTimeout}.");
            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
