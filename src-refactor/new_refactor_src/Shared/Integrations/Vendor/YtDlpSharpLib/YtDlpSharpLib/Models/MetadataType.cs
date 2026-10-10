using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>The yt-dlp <c>_type</c> classifier for a metadata payload.</summary>
[JsonConverter(typeof(MetadataTypeJsonConverter))]
public enum MetadataType
{
    /// <summary>Unknown or unrecognised <c>_type</c> value.</summary>
    Unknown,

    /// <summary>A single video.</summary>
    Video,

    /// <summary>A playlist container.</summary>
    Playlist,

    /// <summary>A multi-video container (e.g., concatenated clips).</summary>
    MultiVideo,

    /// <summary>A pointer to another extractor URL.</summary>
    Url,

    /// <summary>A transparent URL forwarder.</summary>
    UrlTransparent
}
