using Conduit.NATS;
using FluentStorage.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.LiveChat;

/// <summary>
/// Sweeps archived live streams for chat sidecars that have never been ingested and queues an
/// ingest for each. Registered only when live chat is enabled — this is the path that hydrates
/// chat history for a library archived before the feature was turned on, since the Worker stores
/// <c>live_chat.json</c> regardless of the flag.
///
/// Candidates are stored versions of media marked <c>was_live</c>; the sidecar sits next to the
/// primary file, so existence is a single storage probe per candidate.
/// </summary>
public sealed class LiveChatBackfillConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    NpgsqlDataSource dataSource,
    IStoreProvider blobStorageProvider,
    IBackgroundRunReporter runReporter,
    ILogger<LiveChatBackfillConsumerService> logger) : BackgroundService
{
    private const string ChatSidecarFileName = "media.live_chat.json";
    private const string EmoteMapSidecarFileName = "media.live_chat.emotes.json";

    private static readonly StreamName Stream = StreamName.From(BackgroundJobsTopology.StreamNameValue);

    private CancellationToken _stoppingToken = CancellationToken.None;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        logger.LogInformation("Subscribed to live chat backfill requests on stream {Stream}.", Stream.Value);
        return consumer.ConsumePullAsync<LiveChatBackfillRequested>(
            Stream,
            ConsumerName.From(BackgroundJobsTopology.LiveChatBackfillConsumer),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);
    }

    private async Task HandleAsync(IJsMessageContext<LiveChatBackfillRequested> context)
    {
        var message = context.Message;
        await using var run = await runReporter.BeginAsync(message.TaskType, message);
        try
        {
            await run.ReportAsync("Scanning archived live streams for chat replays…");

            var candidates = await LoadCandidatesAsync(message.TargetMediaGuid, message.Force);
            var queued = 0;
            var probed = 0;

            foreach (var candidate in candidates)
            {
                _stoppingToken.ThrowIfCancellationRequested();
                probed++;

                var directory = StorageObjectPath.GetParent(candidate.StoragePath);
                var chatPath = StorageObjectPath.Combine(directory, ChatSidecarFileName);
                var emoteMapPath = StorageObjectPath.Combine(directory, EmoteMapSidecarFileName);

                IStore storage;
                try
                {
                    storage = await blobStorageProvider.GetAsync(candidate.StorageKey, _stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Skipping live chat backfill for {MediaGuid}: storage key '{StorageKey}' is unavailable.",
                        candidate.MediaGuid, candidate.StorageKey);
                    continue;
                }

                if (!await storage.ObjectExists(chatPath))
                    continue;

                var hasEmoteMap = await storage.ObjectExists(emoteMapPath);
                await publisher.PublishAsync(
                    BackgroundJobSubjects.LiveChatIngestRequest,
                    new LiveChatIngestRequested
                    {
                        MediaGuid = candidate.MediaGuid,
                        VersionNum = candidate.VersionNum,
                        StorageKey = candidate.StorageKey,
                        ChatBlobPath = chatPath,
                        EmoteMapBlobPath = hasEmoteMap ? emoteMapPath : null
                    },
                    candidate.MediaGuid.ToString("N"),
                    cancellationToken: _stoppingToken);
                queued++;

                if (queued % 25 == 0)
                    await run.ReportAsync($"Queued {queued} chat replay ingest(s) so far…");
            }

            run.Succeed($"Probed {probed} archived live stream(s); queued {queued} chat replay ingest(s).");
            logger.LogInformation(
                "Live chat backfill probed {Probed} candidate(s) and queued {Queued} ingest(s).",
                probed, queued);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Live chat backfill request {IdempotencyKey} failed; nacking.", message.IdempotencyKey);
            run.Fail(ex.Message);
            await context.NackAsync();
        }
    }

    private async Task<IReadOnlyList<BackfillCandidate>> LoadCandidatesAsync(Guid? targetMediaGuid, bool force)
    {
        // Chat replays only exist for live streams, so was_live keeps the storage probes bounded.
        // DISTINCT ON keeps one (the newest) version per media item.
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT ON (v.media_guid)
                v.media_guid, v.storage_key, v.storage_path, v.version_num
            FROM media.media_content_id_versions v
            JOIN metadata.media_metadata mm ON mm.media_guid = v.media_guid
            WHERE mm.was_live = true
              AND (@target_media_guid IS NULL OR v.media_guid = @target_media_guid)
              AND (@force OR NOT EXISTS (
                    SELECT 1 FROM metadata.media_live_chat lc WHERE lc.media_guid = v.media_guid))
            ORDER BY v.media_guid, v.version_num DESC
            """);
        command.Parameters.AddWithValue("@target_media_guid", (object?)targetMediaGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("@force", force);

        var candidates = new List<BackfillCandidate>();
        await using var reader = await command.ExecuteReaderAsync(_stoppingToken);
        while (await reader.ReadAsync(_stoppingToken))
        {
            candidates.Add(new BackfillCandidate(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return candidates;
    }

    private readonly record struct BackfillCandidate(
        Guid MediaGuid,
        string StorageKey,
        string StoragePath,
        int VersionNum);
}
