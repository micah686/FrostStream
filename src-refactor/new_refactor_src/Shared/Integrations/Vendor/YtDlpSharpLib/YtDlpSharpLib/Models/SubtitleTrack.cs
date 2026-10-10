using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>Metadata for a single subtitle track variant.</summary>
public sealed record SubtitleTrack
{
    /// <summary>The subtitle file extension (e.g., <c>"srt"</c>).</summary>
    [JsonPropertyName("ext")]
    public string? Ext { get; init; }

    /// <summary>The URL where the subtitle data can be retrieved.</summary>
    public string? Url { get; init; }

    /// <summary>Inline subtitle data, when supplied directly.</summary>
    public string? Data { get; init; }

    /// <summary>Human-readable name of the subtitle track.</summary>
    public string? Name { get; init; }

    /// <summary>HTTP headers required to fetch <see cref="Url"/>, when supplied.</summary>
    [JsonPropertyName("http_headers")]
    public IReadOnlyDictionary<string, string>? HttpHeaders { get; init; }

    /// <summary>Captures any yt-dlp subtitle fields not modelled explicitly.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
