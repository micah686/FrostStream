using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>The yt-dlp <c>live_status</c> field value.</summary>
[JsonConverter(typeof(LiveStatusJsonConverter))]
public enum LiveStatus
{
    /// <summary>Unknown or unrecognised live state.</summary>
    Unknown,

    /// <summary>Not a live stream.</summary>
    NotLive,

    /// <summary>Currently live.</summary>
    IsLive,

    /// <summary>Scheduled to go live.</summary>
    IsUpcoming,

    /// <summary>Was live but the stream has ended.</summary>
    WasLive,

    /// <summary>Stream just ended; archive may not yet be available.</summary>
    PostLive
}
