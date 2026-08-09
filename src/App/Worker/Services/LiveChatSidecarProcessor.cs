using System.Buffers;
using System.IO.Hashing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shared.LiveChat;
using Shared.Messaging;

namespace Worker.Services;

/// <summary>
/// Result of processing a <c>media.live_chat.json</c> sidecar: the chat file itself, the
/// optional emote map written next to it, and any non-fatal warnings.
/// </summary>
public sealed record LiveChatSidecars(
    SidecarFileRef Chat,
    SidecarFileRef? EmoteMap,
    IReadOnlyList<DownloadStageWarning> Warnings);

/// <summary>
/// Post-download processing for yt-dlp live chat replays. Streams the JSONL sidecar once to
/// collect the unique custom emotes used in chat, archives each image through
/// <see cref="AssetCacheWriter"/> (content-addressed, deduped across channels), and writes a
/// <c>media.live_chat.emotes.json</c> map so replay works long after the source CDN URLs rot.
/// This runs regardless of whether ClickHouse ingestion is enabled — the sidecars are the
/// durable source of truth that later backfills read.
/// </summary>
public sealed class LiveChatSidecarProcessor(
    AssetCacheWriter assetCacheWriter,
    ILogger<LiveChatSidecarProcessor> logger)
{
    public async Task<LiveChatSidecars?> ProcessAsync(
        string tempDirectory,
        string mediaFileBase,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var chatPath = Path.Combine(tempDirectory, $"{mediaFileBase}.live_chat.json");
        var chatFile = new FileInfo(chatPath);
        if (!chatFile.Exists || chatFile.Length == 0)
            return null;

        var chat = new SidecarFileRef
        {
            TempFileRef = chatFile.FullName,
            FileName = chatFile.Name,
            SizeBytes = chatFile.Length,
            ContentHashXxh128 = await ComputeXxHash128Async(chatFile.FullName)
        };

        try
        {
            var (emoteMap, warnings) = await BuildEmoteMapAsync(chatPath, tempDirectory, mediaFileBase, jobId, cancellationToken);
            return new LiveChatSidecars(chat, emoteMap, warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Emote archival is best-effort: the chat sidecar itself must still be uploaded.
            logger.LogWarning(ex,
                "Live chat emote extraction failed for JobId {JobId}; uploading the chat sidecar without an emote map.",
                jobId);
            return new LiveChatSidecars(chat, EmoteMap: null,
            [
                new DownloadStageWarning
                {
                    Code = "live_chat_emotes_failed",
                    Message = $"Custom emote archival failed: {ex.Message}"
                }
            ]);
        }
    }

    private async Task<(SidecarFileRef? EmoteMap, IReadOnlyList<DownloadStageWarning> Warnings)> BuildEmoteMapAsync(
        string chatPath,
        string tempDirectory,
        string mediaFileBase,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        // First pass: unique custom emotes only — memory stays O(distinct emotes), not O(lines).
        var emotes = new Dictionary<string, LiveChatFragment>(StringComparer.Ordinal);
        using (var reader = new StreamReader(chatPath))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (LiveChatReplayParser.ParseLine(line) is not { } message)
                    continue;

                foreach (var fragment in message.Fragments)
                {
                    if (fragment is { Type: LiveChatFragment.EmoteType, Id.Length: > 0, Url.Length: > 0 })
                        emotes.TryAdd(fragment.Id, fragment);
                }
            }
        }

        if (emotes.Count == 0)
            return (null, []);

        var entries = new List<LiveChatEmoteMapEntry>(emotes.Count);
        var failed = 0;
        foreach (var emote in emotes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stored = await assetCacheWriter.DownloadAndStoreAsync(emote.Url!, AssetKind.Emote, cancellationToken);
                entries.Add(new LiveChatEmoteMapEntry
                {
                    EmoteId = emote.Id!,
                    Name = emote.Name ?? emote.Id!,
                    SourceUrl = emote.Url!,
                    StorageKey = stored.StorageKey,
                    StoragePath = stored.StoragePath,
                    ContentHash = stored.ContentHash
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex,
                    "Could not archive live chat emote {EmoteId} from {Url} for JobId {JobId}; skipping it.",
                    emote.Id, emote.Url, jobId);
            }
        }

        var warnings = failed == 0
            ? (IReadOnlyList<DownloadStageWarning>)[]
            : [new DownloadStageWarning
            {
                Code = "live_chat_emotes_failed",
                Message = $"{failed} of {emotes.Count} custom chat emotes could not be archived."
            }];

        if (entries.Count == 0)
            return (null, warnings);

        var mapPath = Path.Combine(tempDirectory, $"{mediaFileBase}.live_chat.emotes.json");
        await using (var output = File.Create(mapPath))
            await JsonSerializer.SerializeAsync(output, entries, LiveChatFragmentJson.Options, cancellationToken);

        var mapFile = new FileInfo(mapPath);
        var emoteMap = new SidecarFileRef
        {
            TempFileRef = mapFile.FullName,
            FileName = mapFile.Name,
            SizeBytes = mapFile.Length,
            ContentHashXxh128 = await ComputeXxHash128Async(mapFile.FullName)
        };

        logger.LogInformation(
            "Archived {Stored} custom chat emotes ({Failed} failed) for JobId {JobId} → {MapFileName}",
            entries.Count, failed, jobId, mapFile.Name);
        return (emoteMap, warnings);
    }

    private static async Task<string> ComputeXxHash128Async(string path)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                hasher.Append(buffer.AsSpan(0, read));

            Span<byte> hash = stackalloc byte[16];
            hasher.GetCurrentHash(hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
