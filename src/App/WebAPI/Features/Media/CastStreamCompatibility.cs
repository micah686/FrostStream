using System.Buffers.Binary;
using System.Text;
using Shared.Storage;

namespace WebAPI.Features.Media;

/// <summary>
/// Decides whether a stored original can be handed to a cast device as-is: an MP4 whose
/// <c>moov</c> atom precedes <c>mdat</c> (faststart) carrying H.264 video and AAC (or no) audio.
/// The check reads only the head of the file — the codec sample entries live inside the same
/// <c>moov</c> atom whose position proves faststart. Anything unparseable is reported as
/// incompatible so the caller falls back to the HLS rendition.
/// </summary>
public static class CastStreamCompatibility
{
    /// <summary>Bytes we are willing to scan past before finding moov/mdat (malformed-file guard).</summary>
    private const long MaxLeadingScanBytes = 16 * 1024 * 1024;

    /// <summary>Upper bound for a moov atom we will buffer; typical atoms are well under 10 MB.</summary>
    private const int MaxMoovBytes = 64 * 1024 * 1024;

    private static readonly string[] VideoSampleEntries = ["avc1", "avc3"];
    private static readonly string[] AudioSampleEntries = ["mp4a"];

    public static bool HasMp4Extension(string storagePath)
        => Path.GetExtension(storagePath).ToLowerInvariant() is ".mp4" or ".m4v";

    public static async Task<bool> IsDirectStreamableAsync(
        IBlobStorageProvider blobStorageProvider,
        string storageKey,
        string storagePath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!HasMp4Extension(storagePath))
            return false;

        try
        {
            var stream = await MediaBlobServing.OpenBlobOrNullAsync(
                blobStorageProvider, storageKey, storagePath, cancellationToken);
            if (stream is null)
                return false;

            await using (stream)
            {
                return await ProbeStreamAsync(stream, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "MP4 compatibility probe failed for {StorageKey}:{StoragePath}; treating as not direct-streamable.",
                storageKey,
                storagePath);
            return false;
        }
    }

    /// <summary>Container-level probe of an already-opened MP4 stream (extension checks skipped).</summary>
    public static async Task<bool> ProbeStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        long scanned = 0;

        while (scanned <= MaxLeadingScanBytes)
        {
            if (!await TryReadExactlyAsync(stream, header.AsMemory(0, 8), cancellationToken))
                return false;

            scanned += 8;
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            var boxType = Encoding.ASCII.GetString(header, 4, 4);
            long headerLength = 8;

            if (boxSize == 1)
            {
                if (!await TryReadExactlyAsync(stream, header.AsMemory(8, 8), cancellationToken))
                    return false;

                scanned += 8;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerLength = 16;
            }
            else if (boxSize == 0)
            {
                // Box extends to end of file; only mdat legitimately does this.
                return false;
            }

            if (boxSize < headerLength)
                return false;

            switch (boxType)
            {
                case "mdat":
                    // Media data before the movie header: not faststart.
                    return false;
                case "moov":
                {
                    var payloadLength = boxSize - headerLength;
                    if (payloadLength is <= 0 or > MaxMoovBytes)
                        return false;

                    var moov = new byte[payloadLength];
                    if (!await TryReadExactlyAsync(stream, moov, cancellationToken))
                        return false;

                    return MoovDeclaresCompatibleCodecs(moov);
                }
                default:
                    var toSkip = boxSize - headerLength;
                    if (toSkip > MaxLeadingScanBytes - scanned)
                        return false;

                    await SkipAsync(stream, toSkip, cancellationToken);
                    scanned += toSkip;
                    break;
            }
        }

        return false;
    }

    private static bool MoovDeclaresCompatibleCodecs(ReadOnlySpan<byte> moov)
    {
        var entries = new List<string>();
        CollectSampleEntries(moov, entries);

        var hasCompatibleVideo = false;
        foreach (var entry in entries)
        {
            if (VideoSampleEntries.Contains(entry))
            {
                hasCompatibleVideo = true;
            }
            else if (!AudioSampleEntries.Contains(entry))
            {
                // Unknown track codec (hevc, vp9, opus, subtitles muxed as tracks, ...): the cast
                // device may choke on it, so route through the HLS rendition instead.
                return false;
            }
        }

        return hasCompatibleVideo;
    }

    /// <summary>Walks moov → trak → mdia → minf → stbl → stsd and collects sample entry fourccs.</summary>
    private static void CollectSampleEntries(ReadOnlySpan<byte> box, List<string> entries)
    {
        var offset = 0;
        while (offset + 8 <= box.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(box[offset..]);
            var type = Encoding.ASCII.GetString(box.Slice(offset + 4, 4));
            var headerLength = 8;

            if (size == 1)
            {
                if (offset + 16 > box.Length)
                    return;

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(box[(offset + 8)..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = box.Length - offset;
            }

            if (size < headerLength || offset + size > box.Length)
                return;

            var payload = box.Slice(offset + headerLength, (int)(size - headerLength));
            switch (type)
            {
                case "trak" or "mdia" or "minf" or "stbl":
                    CollectSampleEntries(payload, entries);
                    break;
                case "stsd":
                {
                    // FullBox: version+flags (4) + entry_count (4), then plain sample entry boxes.
                    var entryOffset = 8;
                    while (entryOffset + 8 <= payload.Length)
                    {
                        var entrySize = BinaryPrimitives.ReadUInt32BigEndian(payload[entryOffset..]);
                        if (entrySize < 8 || entryOffset + entrySize > payload.Length)
                            break;

                        entries.Add(Encoding.ASCII.GetString(payload.Slice(entryOffset + 4, 4)));
                        entryOffset += (int)entrySize;
                    }

                    break;
                }
            }

            offset += (int)size;
        }
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                return false;

            total += read;
        }

        return true;
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
            return;

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[Math.Min(count, 81920)];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of MP4 while skipping a box.");

            count -= read;
        }
    }
}
