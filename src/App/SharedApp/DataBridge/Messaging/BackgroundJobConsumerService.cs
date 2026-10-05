using DataBridge.Data;
using DataBridge.Search;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using System.Data.Common;
using DataBridge.Persistence;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class BackgroundJobConsumerService(
    IDurableJobConsumer consumer,
    IMessageBus messageBus,
    ApplicationDatabase dataSource,
    IServiceScopeFactory scopes,
    IMetadataRebuildCoordinator rebuildCoordinator,
    IDownloadHistoryPurger historyPurger,
    IImportSessionPurger importSessionPurger,
    INotificationDispatcher notificationDispatcher,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices("databridge")] IBackgroundRunReporter runReporter,
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
        Func<IDurableMessageContext<TMessage>, Task> handler,
        CancellationToken stoppingToken)
        where TMessage : ScheduledBackgroundRequest
        => consumer.ConsumePullAsync(
            Stream,
            ConsumerName.From(consumerName),
            handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleSearchReindexAsync(IDurableMessageContext<SearchReindexRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync(message.TaskType, message);
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

    private async Task HandleDatabaseMaintenanceAsync(IDurableMessageContext<DatabaseMaintenanceRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync(message.TaskType, message);
        try
        {
            await MarkAttemptAsync(message);
            await run.ReportAsync("Running VACUUM (ANALYZE) over the database…");
            await using var command = dataSource.CreateCommand(dataSource.Sql("BackgroundJobConsumerService.HandleDatabaseMaintenanceAsync.1"));
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync(_stoppingToken);
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

    private async Task HandleDatabaseMaintenanceReindexAsync(IDurableMessageContext<DatabaseMaintenanceReindexRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("database_maintenance_reindex", message);
        try
        {
            await MarkAttemptAsync(message);
            await using var connection = await dataSource.OpenConnectionAsync(_stoppingToken);
            var databaseName = connection.Database.Replace("\"", "\"\"", StringComparison.Ordinal);
            await run.ReportAsync($"Reindexing database \"{connection.Database}\" concurrently…");
            await using var command = ApplicationDbCommands.Create(
                dataSource.Sql("BackgroundJobConsumerService.HandleDatabaseMaintenanceReindexAsync.1", databaseName),
                connection);
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync(_stoppingToken);
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

    private async Task HandleDatabaseStaleMediaCleanupAsync(IDurableMessageContext<DatabaseStaleMediaCleanupRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync("database_stale_media_cleanup", message);
        try
        {
            await MarkAttemptAsync(message);
            await run.ReportAsync("Scanning for media rows with no remaining content storage…");
            long deletedCount = 0;
            while (true)
            {
                _stoppingToken.ThrowIfCancellationRequested();
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
                var deleted = await new ApplicationRetention(db).DeleteStaleMediaBatchAsync(_stoppingToken);
                deletedCount += deleted;
                if (deleted < ApplicationBatches.MembershipBatchSize) break;
                await run.ReportAsync($"Deleted {deletedCount} stale media row(s) so far…");
            }
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

    private async Task HandleDownloadHistoryCleanupAsync(IDurableMessageContext<DownloadHistoryCleanupRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync(message.TaskType, message);
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

    private async Task HandleImportSessionCleanupAsync(IDurableMessageContext<ImportSessionCleanupRequested> context)
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
