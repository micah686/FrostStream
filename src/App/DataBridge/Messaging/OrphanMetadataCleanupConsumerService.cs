using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Finalizes media whose content files have remained missing long enough for
/// metadata retention to expire. The missing-file clock comes from unresolved
/// filesystem rescan findings; storage deletion is owned by the Worker/storage
/// layer, not this DataBridge database cleanup.
/// </summary>
public sealed class OrphanMetadataCleanupConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    OrphanMetadataCleanupExecutor cleanupExecutor,
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

            var result = await cleanupExecutor.CleanupAsync(now, CancellationToken.None);

            logger.LogInformation(
                "Completed orphan cleanup for schedule {ScheduleKey}: recorded {MetadataWithoutMedia} metadata-without-media and {MediaWithoutMetadata} media-without-metadata item(s), resolved {Resolved}, moved {Moved}/{MoveFailed} file orphan(s), deleted {DeletedFiles}/{DeleteFileFailed} file orphan(s), deleted {DeletedMedia} media root row(s).",
                message.ScheduleKey,
                result.RecordedMetadataWithoutMediaCount,
                result.RecordedMediaWithoutMetadataCount,
                result.ResolvedCount,
                result.MovedFileCount,
                result.MoveFailedCount,
                result.DeletedFileCount,
                result.FileDeleteFailedCount,
                result.DeletedMediaCount);

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
