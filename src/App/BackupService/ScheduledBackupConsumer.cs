using Conduit.NATS;
using NodaTime;
using Shared.Messaging;

namespace BackupService;

internal sealed class ScheduledBackupConsumer(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    BackupCoordinator coordinator,
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
        try
        {
            await messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
            {
                Key = message.ScheduleKey,
                AttemptedAt = clock.GetCurrentInstant()
            }, cancellationToken: cancellationToken);

            var name = $"scheduled-{message.ScheduleKey}-{message.DueWindowUtc:yyyyMMddHHmmss}";
            var queued = await coordinator.QueueAsync(name, "snapshot", true, message.IdempotencyKey, cancellationToken);
            var completed = await coordinator.WaitAsync(queued.JobId, cancellationToken);
            if (completed.Status != "completed")
                throw new InvalidOperationException(completed.ErrorMessage ?? "Scheduled backup failed.");

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
            await messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
            {
                Key = message.ScheduleKey,
                FailedAt = clock.GetCurrentInstant()
            }, cancellationToken: CancellationToken.None);
            await context.NackAsync();
        }
    }
}
