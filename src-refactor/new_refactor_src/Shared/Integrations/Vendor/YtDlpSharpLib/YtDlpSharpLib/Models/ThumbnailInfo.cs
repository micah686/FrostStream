using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>Metadata for a single thumbnail entry.</summary>
public sealed record ThumbnailInfo
{
    /// <summary>An identifier or sequence number for the thumbnail.</summary>
    public string? Id { get; init; }

    /// <summary>The thumbnail URL.</summary>
    public string? Url { get; init; }

    /// <summary>Width in pixels.</summary>
    public int? Width { get; init; }

    /// <summary>Height in pixels.</summary>
    public int? Height { get; init; }

    /// <summary>yt-dlp's preference score; higher is better.</summary>
    public int? Preference { get; init; }

    /// <summary>The thumbnail file extension, when supplied.</summary>
    [JsonPropertyName("ext")]
    public string? Ext { get; init; }

    /// <summary>Filesize in bytes, when known.</summary>
    public long? Filesize { get; init; }

    /// <summary>HTTP headers required to fetch <see cref="Url"/>, when supplied.</summary>
    [JsonPropertyName("http_headers")]
    public IReadOnlyDictionary<string, string>? HttpHeaders { get; init; }

    /// <summary>Captures any yt-dlp thumbnail fields not modelled explicitly.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
