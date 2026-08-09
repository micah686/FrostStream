using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using ClickHouse.Driver;
using ClickHouse.Driver.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.LiveChat;
using Shared.Messaging;
using Shared.Storage;

namespace DataBridge.LiveChat;

public sealed record LiveChatIngestResult(
    long MessageCount,
    long SkippedLines,
    long FirstOffsetMs,
    long LastOffsetMs,
    int EmoteCount);

/// <summary>
/// Streams a <c>media.live_chat.json</c> sidecar out of blob storage into ClickHouse in batches.
/// Idempotent: each ingest deletes the media's rows first, and the ReplacingMergeTree key
/// (media_guid, video_offset_ms, message_id) absorbs any partial-batch duplicates a crash-retry
/// leaves behind. Fragment payloads are deduplicated into <c>live_chat_message_texts</c> by
/// XxHash64 of the canonical JSON; custom emote fragments are rewritten to the durable blob
/// paths recorded in the emote-map sidecar before hashing.
/// </summary>
public sealed class LiveChatIngestService(
    ClickHouseAccess clickHouse,
    IStoreProvider blobStorageProvider,
    NpgsqlDataSource dataSource,
    IOptions<LiveChatOptions> options,
    ILogger<LiveChatIngestService> logger)
{
    private static readonly string[] MessageColumns =
    [
        "media_guid", "video_offset_ms", "message_id", "message_type", "published_at",
        "author_external_id", "author_name", "author_badges", "fragments_hash",
        "amount_text", "currency", "header_color", "body_color"
    ];

    private static readonly string[] TextColumns = ["fragments_hash", "fragments"];

    private static readonly string[] EmoteColumns =
        ["emote_id", "name", "source_url", "storage_key", "storage_path"];

    private readonly LiveChatOptions _options = options.Value;

    public async Task<LiveChatIngestResult> IngestAsync(
        LiveChatIngestRequested request,
        CancellationToken cancellationToken)
    {
        var storage = await blobStorageProvider.GetAsync(request.StorageKey, cancellationToken);

        var emoteMap = await LoadEmoteMapAsync(storage, request.EmoteMapBlobPath, cancellationToken);

        // Delete-first makes re-imports and backfill re-runs idempotent.
        await using (var connection = clickHouse.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM live_chat_messages WHERE media_guid = {mediaGuid:UUID}";
            delete.AddParameter("mediaGuid", request.MediaGuid);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var batchSize = Math.Max(1_000, _options.IngestBatchSize);
        var insertOptions = new InsertOptions { BatchSize = batchSize };
        var messageRows = new List<object[]>(batchSize);
        var textRows = new List<object[]>();
        var seenTextHashes = new HashSet<ulong>();
        long messageCount = 0, skippedLines = 0, totalLines = 0;
        long firstOffset = long.MaxValue, lastOffset = long.MinValue;

        await using (var stream = await storage.OpenRead(request.ChatBlobPath)
            ?? throw new InvalidOperationException(
                $"Live chat sidecar was not found at {request.StorageKey}:{request.ChatBlobPath}."))
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                totalLines++;
                if (LiveChatReplayParser.ParseLine(line) is not { } message)
                {
                    skippedLines++;
                    continue;
                }

                var fragments = RewriteEmoteFragments(message.Fragments, emoteMap);
                var fragmentsJson = LiveChatFragmentJson.Serialize(fragments);
                var fragmentsHash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(fragmentsJson));
                if (seenTextHashes.Add(fragmentsHash))
                    textRows.Add([fragmentsHash, fragmentsJson]);

                messageRows.Add(
                [
                    request.MediaGuid,
                    message.VideoOffsetMs,
                    message.MessageId,
                    message.Type.ToWireString(),
                    message.TimestampUsec is { } usec
                        ? DateTimeOffset.FromUnixTimeMilliseconds(usec / 1_000).UtcDateTime
                        : DateTime.UnixEpoch,
                    message.AuthorExternalId,
                    message.AuthorName,
                    message.Badges.ToArray(),
                    fragmentsHash,
                    message.AmountText ?? "",
                    "",
                    message.HeaderColor ?? 0u,
                    message.BodyColor ?? 0u
                ]);

                messageCount++;
                firstOffset = Math.Min(firstOffset, message.VideoOffsetMs);
                lastOffset = Math.Max(lastOffset, message.VideoOffsetMs);

                if (messageRows.Count >= batchSize)
                {
                    await FlushAsync(messageRows, textRows, insertOptions, cancellationToken);
                }
            }
        }

        await FlushAsync(messageRows, textRows, insertOptions, cancellationToken);

        if (emoteMap.Count > 0)
        {
            await clickHouse.Client.InsertBinaryAsync(
                clickHouse.Table("live_chat_emotes"),
                EmoteColumns,
                emoteMap.Values.Select(e =>
                    new object[] { e.EmoteId, e.Name, e.SourceUrl, e.StorageKey, e.StoragePath }),
                insertOptions,
                cancellationToken);
        }

        if (messageCount == 0)
            firstOffset = lastOffset = 0;

        await UpsertMarkerAsync(request, messageCount, firstOffset, lastOffset, cancellationToken);

        logger.LogInformation(
            "Live chat ingested for {MediaGuid}: {Messages} messages ({Skipped} of {Total} lines skipped), " +
            "{UniqueTexts} unique fragment payloads, {Emotes} emotes, offsets {First}–{Last} ms.",
            request.MediaGuid, messageCount, skippedLines, totalLines,
            seenTextHashes.Count, emoteMap.Count, firstOffset, lastOffset);

        return new LiveChatIngestResult(messageCount, skippedLines, firstOffset, lastOffset, emoteMap.Count);
    }

    /// <summary>Best-effort removal of a deleted media's chat rows and marker.</summary>
    public async Task DeleteForMediaAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using (var connection = clickHouse.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM live_chat_messages WHERE media_guid = {mediaGuid:UUID}";
            delete.AddParameter("mediaGuid", mediaGuid);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = dataSource.CreateCommand(
            "DELETE FROM metadata.media_live_chat WHERE media_guid = @media_guid");
        command.Parameters.AddWithValue("@media_guid", mediaGuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FlushAsync(
        List<object[]> messageRows,
        List<object[]> textRows,
        InsertOptions insertOptions,
        CancellationToken cancellationToken)
    {
        // Texts go first: a crash between the two inserts must never leave messages pointing at
        // a fragment payload that was never stored.
        if (textRows.Count > 0)
        {
            await clickHouse.Client.InsertBinaryAsync(
                clickHouse.Table("live_chat_message_texts"), TextColumns, textRows.ToArray(),
                insertOptions, cancellationToken);
            textRows.Clear();
        }

        if (messageRows.Count > 0)
        {
            await clickHouse.Client.InsertBinaryAsync(
                clickHouse.Table("live_chat_messages"), MessageColumns, messageRows.ToArray(),
                insertOptions, cancellationToken);
            messageRows.Clear();
        }
    }

    private static IReadOnlyList<LiveChatFragment> RewriteEmoteFragments(
        IReadOnlyList<LiveChatFragment> fragments,
        IReadOnlyDictionary<string, LiveChatEmoteMapEntry> emoteMap)
    {
        if (emoteMap.Count == 0 || fragments.All(static f => f.Type != LiveChatFragment.EmoteType))
            return fragments;

        return fragments
            .Select(fragment =>
                fragment.Type == LiveChatFragment.EmoteType &&
                fragment.Id is { Length: > 0 } id &&
                emoteMap.TryGetValue(id, out var entry)
                    // Durable, content-addressed path replaces the rotting source URL.
                    ? fragment with { Path = entry.StoragePath, Url = null }
                    : fragment)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, LiveChatEmoteMapEntry>> LoadEmoteMapAsync(
        FluentStorage.Storage.IStore storage,
        string? emoteMapBlobPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(emoteMapBlobPath))
            return new Dictionary<string, LiveChatEmoteMapEntry>(StringComparer.Ordinal);

        try
        {
            await using var stream = await storage.OpenRead(emoteMapBlobPath);
            if (stream is null)
                return new Dictionary<string, LiveChatEmoteMapEntry>(StringComparer.Ordinal);

            var entries = await JsonSerializer.DeserializeAsync<List<LiveChatEmoteMapEntry>>(
                stream, LiveChatFragmentJson.Options, cancellationToken) ?? [];
            return entries
                .Where(static e => !string.IsNullOrEmpty(e.EmoteId))
                .DistinctBy(static e => e.EmoteId, StringComparer.Ordinal)
                .ToDictionary(static e => e.EmoteId, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not load the live chat emote map at {Path}; ingesting without emote rewrites.",
                emoteMapBlobPath);
            return new Dictionary<string, LiveChatEmoteMapEntry>(StringComparer.Ordinal);
        }
    }

    private async Task UpsertMarkerAsync(
        LiveChatIngestRequested request,
        long messageCount,
        long firstOffsetMs,
        long lastOffsetMs,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO metadata.media_live_chat
                (media_guid, version_num, message_count, first_offset_ms, last_offset_ms, ingested_at)
            VALUES (@media_guid, @version_num, @message_count, @first_offset_ms, @last_offset_ms, now())
            ON CONFLICT (media_guid) DO UPDATE SET
                version_num = EXCLUDED.version_num,
                message_count = EXCLUDED.message_count,
                first_offset_ms = EXCLUDED.first_offset_ms,
                last_offset_ms = EXCLUDED.last_offset_ms,
                ingested_at = now()
            """);
        command.Parameters.AddWithValue("@media_guid", request.MediaGuid);
        command.Parameters.AddWithValue("@version_num", (object?)request.VersionNum ?? DBNull.Value);
        command.Parameters.AddWithValue("@message_count", messageCount);
        command.Parameters.AddWithValue("@first_offset_ms", firstOffsetMs);
        command.Parameters.AddWithValue("@last_offset_ms", lastOffsetMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
