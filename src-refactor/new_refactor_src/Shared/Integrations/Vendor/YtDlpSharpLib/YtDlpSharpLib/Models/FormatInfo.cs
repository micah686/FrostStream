using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>Metadata for a single yt-dlp format entry.</summary>
public sealed record FormatInfo
{
    /// <summary>Direct media URL, when supplied.</summary>
    public string? Url { get; init; }

    /// <summary>The yt-dlp format identifier (e.g., <c>"137"</c>, <c>"bestaudio"</c>).</summary>
    [JsonPropertyName("format_id")]
    public string? FormatId { get; init; }

    /// <summary>The format description (e.g., <c>"137 - 1920x1080"</c>).</summary>
    public string? Format { get; init; }

    /// <summary>Human-readable note describing the format (e.g., <c>"1080p"</c>).</summary>
    [JsonPropertyName("format_note")]
    public string? FormatNote { get; init; }

    /// <summary>The file extension (e.g., <c>"mp4"</c>).</summary>
    [JsonPropertyName("ext")]
    public string? Ext { get; init; }

    /// <summary>The video resolution (e.g., <c>"1920x1080"</c>).</summary>
    public string? Resolution { get; init; }

    /// <summary>Width in pixels, when known.</summary>
    public int? Width { get; init; }

    /// <summary>Height in pixels, when known.</summary>
    public int? Height { get; init; }

    /// <summary>Filesize in bytes, when known.</summary>
    public long? Filesize { get; init; }

    /// <summary>Approximate filesize in bytes, when an exact value is not available.</summary>
    [JsonPropertyName("filesize_approx")]
    public long? FilesizeApprox { get; init; }

    /// <summary>Frames per second, when known.</summary>
    public double? Fps { get; init; }

    /// <summary>Total bitrate (kbps), when known.</summary>
    public double? Tbr { get; init; }

    /// <summary>Video bitrate (kbps), when known.</summary>
    public double? Vbr { get; init; }

    /// <summary>Audio bitrate (kbps), when known.</summary>
    public double? Abr { get; init; }

    /// <summary>Audio sample rate (Hz), when known.</summary>
    public int? Asr { get; init; }

    /// <summary>The video codec, when applicable.</summary>
    public string? Vcodec { get; init; }

    /// <summary>The audio codec, when applicable.</summary>
    public string? Acodec { get; init; }

    /// <summary>Dynamic range descriptor, e.g., <c>"HDR"</c>.</summary>
    [JsonPropertyName("dynamic_range")]
    public string? DynamicRange { get; init; }

    /// <summary>Audio file extension, when distinct from <see cref="Ext"/>.</summary>
    [JsonPropertyName("audio_ext")]
    public string? AudioExt { get; init; }

    /// <summary>Video file extension, when distinct from <see cref="Ext"/>.</summary>
    [JsonPropertyName("video_ext")]
    public string? VideoExt { get; init; }

    /// <summary>Streaming protocol used to fetch the format.</summary>
    public string? Protocol { get; init; }

    /// <summary>Audio language, when known.</summary>
    public string? Language { get; init; }

    /// <summary>Quality score assigned by yt-dlp.</summary>
    public double? Quality { get; init; }

    /// <summary>yt-dlp's preference score; higher is better.</summary>
    public int? Preference { get; init; }

    /// <summary>The URL of the player needed for this format, when supplied.</summary>
    [JsonPropertyName("player_url")]
    public string? PlayerUrl { get; init; }

    /// <summary>HTTP headers required to fetch the URL, when supplied.</summary>
    [JsonPropertyName("http_headers")]
    public IReadOnlyDictionary<string, string>? HttpHeaders { get; init; }

    /// <summary>Captures any yt-dlp format fields not modelled explicitly.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
