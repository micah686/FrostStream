using DataBridge.Backup;
using DataBridge.Search;
using Conduit.NATS;
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
    WatchedItemAutoDeleteExecutor watchedAutoDeleteExecutor,
    BackupRunner backupRunner,
    INotificationDispatcher notificationDispatcher,
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
            Consume<StaleDatabaseCleanupRequested>(BackgroundJobsTopology.StaleDatabaseCleanupConsumer, HandleStaleDatabaseCleanupAsync, stoppingToken),
            Consume<WatchedItemAutoDeleteRequested>(BackgroundJobsTopology.WatchedItemAutoDeleteConsumer, HandleWatchedItemAutoDeleteAsync, stoppingToken),
            Consume<ProcessedMessageCleanupRequested>(BackgroundJobsTopology.ProcessedMessageCleanupConsumer, HandleProcessedMessageCleanupAsync, stoppingToken),
            Consume<BackupRequested>(BackgroundJobsTopology.DataBridgeBackupConsumer, HandleBackupAsync, stoppingToken)
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

            // Await the rebuild so the schedule is only marked completed once the
            // synchronous index rebuild actually finishes (not just when accepted).
            var result = await rebuildCoordinator.RebuildAsync(
                $"background job {message.IdempotencyKey}",
                CancellationToken.None);
            if (!result.Accepted)
            {
                logger.LogWarning("Typesense reindex request {IdempotencyKey} was not accepted: {Error}", message.IdempotencyKey, result.ErrorMessage);
                await MarkFailureAsync(message, result.ErrorMessage);
                await context.NackAsync();
                return;
            }

            await MarkSuccessAsync(message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling Typesense reindex request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await MarkFailureAsync(message);
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
            await MarkFailureAsync(message);
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
                    FROM media.media m
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM media.media_content_id_versions civ
                        WHERE civ.media_guid = m.media_guid
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM media.media_source_versions sv
                        JOIN downloads.download_jobs dj ON dj.job_id = sv.latest_job_id
                        WHERE sv.media_guid = m.media_guid
                        AND dj.state::text = ANY(@active_download_job_states)
                    )
                ),
                deleted AS (
                    DELETE FROM media.media m
                    USING candidates c
                    WHERE m.media_guid = c.media_guid
                    RETURNING m.media_guid
                )
                SELECT count(*)::bigint FROM deleted;
                """);
            DownloadJobStateSql.AddActiveStatesParameter(command);
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
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleProcessedMessageCleanupAsync(IJsMessageContext<ProcessedMessageCleanupRequested> context)
    {
        var message = context.Message;
        try
        {
            await MarkAttemptAsync(message);

            var cutoff = clock.GetCurrentInstant().Minus(Duration.FromDays(30));
            await using var command = dataSource.CreateCommand(
                "DELETE FROM downloads.processed_messages WHERE processed_at < @cutoff;");
            command.Parameters.AddWithValue("cutoff", cutoff.ToDateTimeOffset());
            command.CommandTimeout = 0;
            var deletedCount = await command.ExecuteNonQueryAsync();

            await MarkSuccessAsync(message);
            logger.LogInformation(
                "Deleted {Count} processed message row(s) older than {Cutoff} for background request {IdempotencyKey}.",
                deletedCount,
                cutoff,
                message.IdempotencyKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling processed message cleanup request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleWatchedItemAutoDeleteAsync(IJsMessageContext<WatchedItemAutoDeleteRequested> context)
    {
        var message = context.Message;
        try
        {
            await MarkAttemptAsync(message);
            var response = await watchedAutoDeleteExecutor.CleanupAsync(CancellationToken.None);
            if (!response.Success)
            {
                logger.LogWarning(
                    "Watched item auto-delete request {IdempotencyKey} failed: {ErrorCode} {ErrorMessage}",
                    message.IdempotencyKey,
                    response.ErrorCode,
                    response.ErrorMessage);
                await MarkFailureAsync(message);
                await context.NackAsync();
                return;
            }

            await MarkSuccessAsync(message);
            logger.LogInformation(
                "Watched item auto-delete request {IdempotencyKey} completed: deleted {DeletedCount}, failed {FailedCount}, files {FilesDeleted}.",
                message.IdempotencyKey,
                response.Result?.DeletedCount ?? 0,
                response.Result?.FailedCount ?? 0,
                response.Result?.FilesDeleted ?? 0);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling watched item auto-delete request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleBackupAsync(IJsMessageContext<BackupRequested> context)
    {
        var message = context.Message;
        try
        {
            await MarkAttemptAsync(message);
            var name = $"scheduled-{message.ScheduleKey}-{message.DueWindowUtc:yyyyMMddHHmmss}";
            var (success, archivePath, errorMessage) = await backupRunner.RunAsync(name, CancellationToken.None);
            if (!success)
            {
                logger.LogError("Backup failed for schedule {ScheduleKey}: {ErrorMessage}", message.ScheduleKey, errorMessage);
                await MarkFailureAsync(message, errorMessage);
                await context.NackAsync();
                return;
            }

            logger.LogInformation("Backup completed for schedule {ScheduleKey}. Archive: {ArchivePath}", message.ScheduleKey, archivePath);
            await MarkSuccessAsync(message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling backup request {IdempotencyKey}; nacking", message.IdempotencyKey);
            await MarkFailureAsync(message);
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

    private async Task MarkFailureAsync(ScheduledBackgroundRequest message, string? failureMessage = null)
    {
        await messageBus.PublishAsync(ScheduleSubjects.MarkFailure, new ScheduleMarkFailureRequestMessage
        {
            Key = message.ScheduleKey,
            FailedAt = clock.GetCurrentInstant()
        });
        await notificationDispatcher.NotifyScheduleFailureAsync(
            message.ScheduleKey,
            failureMessage ?? $"Background request {message.IdempotencyKey} failed.");
    }
}
