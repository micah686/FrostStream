using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class OrphanMetadataCleanupExecutor(NpgsqlDataSource dataSource)
{
    public async Task<OrphanMetadataCleanupResult> CleanupAsync(
        Instant cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            WITH candidates AS (
                SELECT m.media_guid
                FROM media m
                WHERE EXISTS (
                    SELECT 1
                    FROM media_content_id_versions civ
                    WHERE civ.media_guid = m.media_guid
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM media_content_id_versions civ
                    WHERE civ.media_guid = m.media_guid
                    AND NOT EXISTS (
                        SELECT 1
                        FROM filesystem_rescan_findings finding
                        WHERE finding.media_guid = civ.media_guid
                          AND finding.storage_key = civ.storage_key
                          AND finding.storage_path = civ.storage_path
                          AND finding.finding_type = 'MissingFile'
                          AND finding.resolved_at IS NULL
                          AND finding.detected_at <= @cutoff
                    )
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
            candidate_count AS (
                SELECT count(*)::bigint AS count FROM candidates
            ),
            deleted AS (
                DELETE FROM media m
                USING candidates c
                WHERE m.media_guid = c.media_guid
                RETURNING m.media_guid
            )
            SELECT
                (SELECT count FROM candidate_count) AS candidate_count,
                count(deleted.media_guid)::bigint AS deleted_count
            FROM deleted;
            """);
        command.Parameters.AddWithValue("cutoff", cutoff.ToDateTimeOffset());
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new OrphanMetadataCleanupResult(0, 0);
        }

        return new OrphanMetadataCleanupResult(reader.GetInt64(0), reader.GetInt64(1));
    }
}

public sealed record OrphanMetadataCleanupResult(long CandidateCount, long DeletedCount);

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
    private static readonly Duration RetentionPeriod = Duration.FromDays(30);
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

            var cutoff = now.Minus(RetentionPeriod);
            var result = await cleanupExecutor.CleanupAsync(cutoff, CancellationToken.None);

            logger.LogInformation(
                "Deleted {DeletedCount} orphan media root row(s) from {CandidateCount} candidate(s) for schedule {ScheduleKey}. Missing-file cutoff: {Cutoff}.",
                result.DeletedCount,
                result.CandidateCount,
                message.ScheduleKey,
                cutoff);

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
