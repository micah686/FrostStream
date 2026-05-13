using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class OrphanMetadataCleanupConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
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

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            var cutoff = now.Minus(Duration.FromDays(30));
            var deleted = await db.ProcessedMessages
                .Where(x => x.ProcessedAt < cutoff)
                .ExecuteDeleteAsync();

            await messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
            {
                Key = message.ScheduleKey,
                SucceededAt = clock.GetCurrentInstant()
            });

            logger.LogInformation(
                "Deleted {Count} processed message rows older than {Cutoff} for schedule {ScheduleKey}.",
                deleted,
                cutoff,
                message.ScheduleKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling orphan metadata cleanup for schedule {ScheduleKey}; nacking", message.ScheduleKey);
            await context.NackAsync();
        }
    }
}
