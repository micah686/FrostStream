using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Messaging;

namespace WebAPI.Features.Media;

/// <summary>
/// Resolves (and optionally queues) cached audio renditions through DataBridge. Shared by the
/// progressive watch endpoint's audio mode and every HLS endpoint.
/// </summary>
public sealed class AudioRenditionResolver(IMessageBus messageBus, ILogger<AudioRenditionResolver> logger)
{
    private static readonly TimeSpan AudioQueryTimeout = TimeSpan.FromSeconds(30);

    public async Task<(IActionResult? Error, AudioRenditionDto? Rendition)> ResolveAsync(
        Guid mediaGuid,
        AudioRenditionFormat format,
        string? storageKey,
        int? sourceVersion,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await messageBus.RequestAsync<AudioRenditionResolveRequest, AudioRenditionResolveResponse>(
                AudioRenditionSubjects.Resolve,
                new AudioRenditionResolveRequest
                {
                    MediaGuid = mediaGuid,
                    Format = format,
                    StorageKey = string.IsNullOrWhiteSpace(storageKey) ? null : storageKey.Trim(),
                    SourceVersion = sourceVersion,
                    CreateIfMissing = createIfMissing
                },
                AudioQueryTimeout,
                cancellationToken);

            if (response is null)
                return (Unavailable(), null);

            if (!response.Success)
            {
                return (response.ErrorCode == "not_found"
                    ? new NotFoundObjectResult(response.ErrorMessage ?? "Audio rendition source was not found.")
                    : new ObjectResult(response.ErrorMessage ?? "Audio rendition lookup failed.")
                    {
                        StatusCode = StatusCodes.Status500InternalServerError
                    }, null);
            }

            return response.Item is null
                ? (new ObjectResult("DataBridge returned an invalid audio rendition response.")
                {
                    StatusCode = StatusCodes.Status502BadGateway
                }, null)
                : (null, response.Item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed resolving audio rendition for {MediaGuid}.", mediaGuid);
            return (Unavailable(), null);
        }
    }

    private static ObjectResult Unavailable()
        => new("DataBridge is unreachable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
}

/// <summary>Format parsing, content types, and rendition storage-layout helpers.</summary>
public static class AudioRenditionHelpers
{
    public const string DefaultFormat = "aac";

    public static bool TryParseAudioFormat(string? value, out AudioRenditionFormat format)
    {
        format = AudioRenditionFormat.Aac;
        return value?.Trim().ToLowerInvariant() switch
        {
            "aac" or "m4a" => Set(out format, AudioRenditionFormat.Aac),
            "opus" => Set(out format, AudioRenditionFormat.Opus),
            "mp3" => Set(out format, AudioRenditionFormat.Mp3),
            _ => false
        };
    }

    private static bool Set(out AudioRenditionFormat target, AudioRenditionFormat value)
    {
        target = value;
        return true;
    }

    public static string AudioFormatToken(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Opus => "opus",
            AudioRenditionFormat.Mp3 => "mp3",
            _ => "aac"
        };

    public static string AudioContentType(AudioRenditionFormat format)
        => format switch
        {
            AudioRenditionFormat.Aac => "audio/mp4",
            AudioRenditionFormat.Opus => "audio/ogg",
            AudioRenditionFormat.Mp3 => "audio/mpeg",
            _ => "application/octet-stream"
        };

    public static bool IsSafeHlsFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            return false;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".ts" or ".m4s" or ".mp4";
    }

    public static string HlsFileContentType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".ts" => "video/mp2t",
            ".m4s" => "video/iso.segment",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

    public static string CombineStoragePath(string directory, string fileName)
        => string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory.TrimEnd('/')}/{fileName}";

    public static string HlsStorageDirectory(AudioRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? "hls"
            : CombineStoragePath(StorageDirectory(rendition.StoragePath), "hls");

    public static string? HlsManifestStoragePath(AudioRenditionDto rendition)
        => string.IsNullOrWhiteSpace(rendition.StoragePath)
            ? null
            : CombineStoragePath(HlsStorageDirectory(rendition), "index.m3u8");

    private static string StorageDirectory(string storagePath)
    {
        var slash = storagePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : storagePath[..slash];
    }
}
