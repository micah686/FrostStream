using Conduit.NATS;
using NodaTime;
using Shared.Messaging;

namespace BackupService;

internal sealed class ScheduledBackupConsumer(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    BackupCoordinator coordinator,
    IBackgroundRunReporter runReporter,
    IClock clock,
    ILogger<ScheduledBackupConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<BackupRequested>(
            StreamName.From(BackgroundJobsTopology.StreamNameValue),
            ConsumerName.From(BackgroundJobsTopology.BackupServiceBackupConsumer),
            context => HandleAsync(context, stoppingToken),
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<BackupRequested> context, CancellationToken cancellationToken)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("backup", message, "snapshot", cancellationToken);
        try
        {
            await messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
            {
                Key = message.ScheduleKey,
                AttemptedAt = clock.GetCurrentInstant()
            }, cancellationToken: cancellationToken);

            var name = $"scheduled-{message.ScheduleKey}-{message.DueWindowUtc:yyyyMMddHHmmss}";
            await run.ReportAsync($"Queueing snapshot backup '{name}'…");
            var queued = await coordinator.QueueAsync(name, "snapshot", true, message.IdempotencyKey, cancellationToken);
            await run.ReportAsync("Backup running; waiting for the archive to finish…");
            var completed = await coordinator.WaitAsync(queued.JobId, cancellationToken);
            if (completed.Status != "completed")
                throw new InvalidOperationException(completed.ErrorMessage ?? "Scheduled backup failed.");

            run.Succeed(completed.ArchivePath is { } path ? $"Archive written to {path}." : "Backup completed.");
            await messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
            {
                Key = message.ScheduleKey,
                SucceededAt = clock.GetCurrentInstant()
            }, cancellationToken: cancellationToken);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled backup {IdempotencyKey} failed.", message.IdempotencyKey);
            run.Fail(ex.Message);
            await messageBus.PublishAsync(NotificationSubjects.DispatchAdmin, new NotificationDispatchAdminEventMessage
            {
                EventKey = NotificationEventKeys.BackupFailed,
                Subject = "FrostStream backup failed",
                Body = $"Scheduled backup '{message.ScheduleKey}' failed: {ex.Message}"
            }, cancellationToken: CancellationToken.None);
            await messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
            {
                Key = message.ScheduleKey,
                FailedAt = clock.GetCurrentInstant()
            }, cancellationToken: CancellationToken.None);
            await context.NackAsync();
        }
    }
}
