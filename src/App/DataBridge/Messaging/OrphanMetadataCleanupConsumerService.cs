using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Pull-consumer for orphan-metadata-cleanup trigger commands published by Scheduler.
///
/// Cleanup target (this PR): rows in the <c>processed_messages</c> dedupe table
/// older than <see cref="ProcessedMessageRetention"/>. Those rows are dedupe markers
/// from the download flow; once their owning job has closed out, they're pure
/// baggage. Future cleanup kinds (orphan media rows, dangling search docs, etc.)
/// can be added here behind a switch — keeping all "cleanup" semantics in one
/// service that owns its scoped DbContext.
///
/// On success the service publishes <see cref="ScheduleSubjects.MarkSuccess"/> so
/// DataBridge advances the schedule's <c>last_success_at</c> / <c>next_due_at</c>.
/// JetStream stream-level dedupe via <c>Nats-Msg-Id</c> already prevents duplicate
/// runs of the same trigger window, so no per-message ledger is needed.
/// </summary>
public sealed class OrphanMetadataCleanupConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<OrphanMetadataCleanupConsumerService> logger) : BackgroundService
{
    private static readonly Duration ProcessedMessageRetention = Duration.FromDays(30);

    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<OrphanMetadataCleanupRequested>(
            stream: Stream,
            consumer: ConsumerName.From(BackgroundJobsTopology.OrphanMetadataCleanupConsumer),
            handler: HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<OrphanMetadataCleanupRequested> context)
    {
        var msg = context.Message;
        var startedAt = clock.GetCurrentInstant();

        try
        {
            logger.LogInformation(
                "OrphanMetadataCleanup started for ScheduleKey {ScheduleKey} CorrelationId {CorrelationId} IdempotencyKey {IdempotencyKey}",
                msg.ScheduleKey, msg.CorrelationId, msg.IdempotencyKey);

            var deleted = await RunCleanupAsync(startedAt);

            logger.LogInformation(
                "OrphanMetadataCleanup completed for ScheduleKey {ScheduleKey} DeletedRows {DeletedRows} ElapsedMs {ElapsedMs}",
                msg.ScheduleKey,
                deleted,
                (clock.GetCurrentInstant() - startedAt).TotalMilliseconds);

            await messageBus.PublishAsync(
                ScheduleSubjects.MarkSuccess,
                new ScheduleMarkSuccessRequestMessage
                {
                    Key = msg.ScheduleKey,
                    SucceededAt = clock.GetCurrentInstant(),
                    IdempotencyKey = msg.IdempotencyKey
                });

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "OrphanMetadataCleanup failed for ScheduleKey {ScheduleKey} IdempotencyKey {IdempotencyKey}",
                msg.ScheduleKey, msg.IdempotencyKey);
            // Don't ack — let JetStream redeliver until MaxDeliver, then the message
            // moves to the DLQ (or expires under WorkQueue retention).
            await context.NackAsync();
        }
    }

    private async Task<int> RunCleanupAsync(Instant now)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();

        var cutoff = now - ProcessedMessageRetention;

        return await db.ProcessedMessages
            .Where(x => x.ProcessedAt < cutoff)
            .ExecuteDeleteAsync();
    }
}
