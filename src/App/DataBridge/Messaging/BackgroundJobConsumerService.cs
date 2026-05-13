using DataBridge.Search;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class BackgroundJobConsumerService(
    IJetStreamConsumer consumer,
    IMessageBus messageBus,
    NpgsqlDataSource dataSource,
    IMetadataRebuildCoordinator rebuildCoordinator,
    IClock clock,
    ILogger<BackgroundJobConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Consume<SearchReindexRequested>(BackgroundJobsTopology.SearchReindexConsumer, HandleSearchReindexAsync, stoppingToken),
            Consume<DatabaseMaintenanceRequested>(BackgroundJobsTopology.DatabaseMaintenanceConsumer, HandleDatabaseMaintenanceAsync, stoppingToken),
            Consume<StaleDatabaseCleanupRequested>(BackgroundJobsTopology.StaleDatabaseCleanupConsumer, HandleStaleDatabaseCleanupAsync, stoppingToken)
        };

        logger.LogInformation("Subscribed to {Count} background job consumers on stream {Stream}.", consumers.Length, Stream.Value);
        return Task.WhenAll(consumers);
    }

    private Task Consume<TMessage>(
        string consumerName,
        Func<IJsMessageContext<TMessage>, Task> handler,
        CancellationToken stoppingToken)
        where TMessage : ScheduledBackgroundRequest
        => consumer.ConsumePullAsync(
            Stream,
            ConsumerName.From(consumerName),
            handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleSearchReindexAsync(IJsMessageContext<SearchReindexRequested> context)
    {
        var message = context.Message;
        try
        {
            await MarkAttemptAsync(message);
            var result = rebuildCoordinator.StartRebuild($"background job {message.IdempotencyKey}");
            if (!result.Accepted)
            {
                logger.LogWarning("Typesense reindex request {IdempotencyKey} was not accepted: {Error}", message.IdempotencyKey, result.ErrorMessage);
                await context.NackAsync();
                return;
            }

            await MarkSuccessAsync(message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling Typesense reindex request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await context.NackAsync();
        }
    }

    private async Task HandleDatabaseMaintenanceAsync(IJsMessageContext<DatabaseMaintenanceRequested> context)
    {
        var message = context.Message;
        try
        {
            await MarkAttemptAsync(message);
            await using var command = dataSource.CreateCommand("VACUUM (ANALYZE);");
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync();
            await MarkSuccessAsync(message);
            logger.LogInformation("Completed PostgreSQL VACUUM ANALYZE for background request {IdempotencyKey}.", message.IdempotencyKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling database maintenance request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await context.NackAsync();
        }
    }

    private async Task HandleStaleDatabaseCleanupAsync(IJsMessageContext<StaleDatabaseCleanupRequested> context)
    {
        var message = context.Message;
        try
        {
            await MarkAttemptAsync(message);
            await using var command = dataSource.CreateCommand("""
                WITH candidates AS (
                    SELECT m.media_guid
                    FROM media m
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM media_content_id_versions civ
                        WHERE civ.media_guid = m.media_guid
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM media_source_versions sv
                        JOIN download_jobs dj ON dj.job_id = sv.latest_job_id
                        WHERE sv.media_guid = m.media_guid
                        AND dj.state IN (
                            'queued',
                            'metadata_pending',
                            'metadata_resolved',
                            'download_pending',
                            'downloaded_temp',
                            'upload_pending',
                            'uploaded',
                            'commit_pending',
                            'compensating',
                            'failed_transient'
                        )
                    )
                ),
                deleted AS (
                    DELETE FROM media m
                    USING candidates c
                    WHERE m.media_guid = c.media_guid
                    RETURNING m.media_guid
                )
                SELECT count(*)::bigint FROM deleted;
                """);
            command.CommandTimeout = 0;
            var deletedCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
            await MarkSuccessAsync(message);
            logger.LogInformation(
                "Deleted {Count} stale media root row(s) with no content storage for background request {IdempotencyKey}.",
                deletedCount,
                message.IdempotencyKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling stale database cleanup request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await context.NackAsync();
        }
    }

    private Task MarkAttemptAsync(ScheduledBackgroundRequest message)
        => messageBus.PublishAsync(ScheduleSubjects.MarkAttempt, new ScheduleMarkAttemptRequestMessage
        {
            Key = message.ScheduleKey,
            AttemptedAt = clock.GetCurrentInstant()
        });

    private Task MarkSuccessAsync(ScheduledBackgroundRequest message)
        => messageBus.PublishAsync(ScheduleSubjects.MarkSuccess, new ScheduleMarkSuccessRequestMessage
        {
            Key = message.ScheduleKey,
            SucceededAt = clock.GetCurrentInstant()
        });
}
