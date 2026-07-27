using DataBridge.Data;
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
    IDownloadHistoryPurger historyPurger,
    IImportSessionPurger importSessionPurger,
    INotificationDispatcher notificationDispatcher,
    IBackgroundRunReporter runReporter,
    IClock clock,
    ILogger<BackgroundJobConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    /// <summary>Handlers do not receive the host token, so long-running work reads it from here to stay interruptible on shutdown.</summary>
    private CancellationToken _stoppingToken = CancellationToken.None;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        var consumers = new[]
        {
            Consume<SearchReindexRequested>(BackgroundJobsTopology.SearchReindexConsumer, HandleSearchReindexAsync, stoppingToken),
            Consume<DatabaseMaintenanceRequested>(BackgroundJobsTopology.DatabaseMaintenanceConsumer, HandleDatabaseMaintenanceAsync, stoppingToken),
            Consume<DatabaseMaintenanceReindexRequested>(BackgroundJobsTopology.DatabaseMaintenanceReindexConsumer, HandleDatabaseMaintenanceReindexAsync, stoppingToken),
            Consume<DatabaseStaleMediaCleanupRequested>(BackgroundJobsTopology.DatabaseStaleMediaCleanupConsumer, HandleDatabaseStaleMediaCleanupAsync, stoppingToken),
            Consume<DownloadHistoryCleanupRequested>(BackgroundJobsTopology.DownloadHistoryCleanupConsumer, HandleDownloadHistoryCleanupAsync, stoppingToken),
            Consume<ImportSessionCleanupRequested>(BackgroundJobsTopology.ImportSessionCleanupConsumer, HandleImportSessionCleanupAsync, stoppingToken)
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
        await using var run = await runReporter.BeginAsync("search_reindex", message);
        try
        {
            await MarkAttemptAsync(message);
            await run.ReportAsync("Rebuilding the Typesense search index…");

            // Await the rebuild so the schedule is only marked completed once the
            // synchronous index rebuild actually finishes (not just when accepted).
            var result = await rebuildCoordinator.RebuildAsync(
                $"background job {message.IdempotencyKey}",
                CancellationToken.None);
            if (!result.Accepted)
            {
                logger.LogWarning("Typesense reindex request {IdempotencyKey} was not accepted: {Error}", message.IdempotencyKey, result.ErrorMessage);
                run.Fail(result.ErrorMessage ?? "The search index rebuild was not accepted.");
                await MarkFailureAsync(message, result.ErrorMessage);
                await context.NackAsync();
                return;
            }

            run.Succeed("Search index rebuilt.");
            await MarkSuccessAsync(message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling Typesense reindex request {IdempotencyKey}; nacking", message.IdempotencyKey);
            run.Fail(ex.Message);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleDatabaseMaintenanceAsync(IJsMessageContext<DatabaseMaintenanceRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("database_maintenance", message);
        try
        {
            await MarkAttemptAsync(message);
            await run.ReportAsync("Running VACUUM (ANALYZE) over the database…");
            await using var command = dataSource.CreateCommand("VACUUM (ANALYZE);");
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync();
            run.Succeed("VACUUM (ANALYZE) completed.");
            await MarkSuccessAsync(message);
            logger.LogInformation("Completed PostgreSQL VACUUM ANALYZE for background request {IdempotencyKey}.", message.IdempotencyKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling database maintenance request {IdempotencyKey}; nacking", message.IdempotencyKey);
            run.Fail(ex.Message);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleDatabaseMaintenanceReindexAsync(IJsMessageContext<DatabaseMaintenanceReindexRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("database_maintenance_reindex", message);
        try
        {
            await MarkAttemptAsync(message);
            await using var connection = await dataSource.OpenConnectionAsync();
            var databaseName = connection.Database.Replace("\"", "\"\"", StringComparison.Ordinal);
            await run.ReportAsync($"Reindexing database \"{connection.Database}\" concurrently…");
            await using var command = new NpgsqlCommand(
                $"REINDEX DATABASE CONCURRENTLY \"{databaseName}\";",
                connection)
            {
                CommandTimeout = 0
            };
            await command.ExecuteNonQueryAsync();
            run.Succeed("Database reindex completed.");
            await MarkSuccessAsync(message);
            logger.LogInformation(
                "Completed PostgreSQL REINDEX DATABASE CONCURRENTLY for background request {IdempotencyKey}.",
                message.IdempotencyKey);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling database reindex request {IdempotencyKey}; nacking", message.IdempotencyKey);
            run.Fail(ex.Message);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleDatabaseStaleMediaCleanupAsync(IJsMessageContext<DatabaseStaleMediaCleanupRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("database_stale_media_cleanup", message);
        try
        {
            await MarkAttemptAsync(message);
            await run.ReportAsync("Scanning for media rows with no remaining content storage…");
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
                        JOIN jobs.download_jobs dj ON dj.job_id = sv.latest_job_id
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
            run.Succeed($"Deleted {deletedCount} stale media row(s).");
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
            run.Fail(ex.Message);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleDownloadHistoryCleanupAsync(IJsMessageContext<DownloadHistoryCleanupRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("download_history_cleanup", message);
        try
        {
            await MarkAttemptAsync(message);

            var result = await historyPurger.PurgeAsync(
                message.RetentionDays ?? DownloadHistoryPurger.DefaultRetentionDays,
                message.IncludeFailed,
                progress => run.ReportAsync(progress),
                _stoppingToken);

            run.Succeed(result.Describe());
            await MarkSuccessAsync(message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling download history cleanup request {IdempotencyKey}; nacking", message.IdempotencyKey);
            run.Fail(ex.Message);
            await MarkFailureAsync(message);
            await context.NackAsync();
        }
    }

    private async Task HandleImportSessionCleanupAsync(IJsMessageContext<ImportSessionCleanupRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("import_session_cleanup", message);
        try
        {
            await MarkAttemptAsync(message);

            var result = await importSessionPurger.PurgeAsync(
                message.RetentionDays ?? ImportSessionPurger.DefaultRetentionDays,
                progress => run.ReportAsync(progress),
                _stoppingToken);

            run.Succeed(result.Describe());
            await MarkSuccessAsync(message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling import session cleanup request {IdempotencyKey}; nacking", message.IdempotencyKey);
            run.Fail(ex.Message);
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
