using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Placeholder consumer for the <c>orphan_metadata_cleanup</c> task. Cleaning up
/// metadata that has no associated media file is not yet implemented; the previous
/// processed-message purge logic now lives in <see cref="BackgroundJobConsumerService"/>
/// under the <c>processed_message_cleanup</c> task. This handler drains and
/// acknowledges requests (marking the schedule) so the queue does not back up.
/// </summary>
public sealed class OrphanMetadataCleanupConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IClock clock,
    ILogger<OrphanMetadataCleanupConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<OrphanMetadataCleanupRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.OrphanMetadataCleanupConsumer),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<OrphanMetadataCleanupRequested> context)
    {
        var message = context.Message;
        try
        {
            var now = clock.GetCurrentInstant();
            await messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
            {
                Key = message.ScheduleKey,
                AttemptedAt = now
            });

            // No-op placeholder: orphan metadata cleanup is not yet implemented.
            logger.LogInformation(
                "Orphan metadata cleanup is a no-op placeholder; acknowledging schedule {ScheduleKey}.",
                message.ScheduleKey);

            await messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
            {
                Key = message.ScheduleKey,
                SucceededAt = clock.GetCurrentInstant()
            });

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling orphan metadata cleanup for schedule {ScheduleKey}; nacking", message.ScheduleKey);
            await messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
            {
                Key = message.ScheduleKey,
                FailedAt = clock.GetCurrentInstant()
            });
            await context.NackAsync();
        }
    }
}
