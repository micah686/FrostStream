using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>The yt-dlp <c>availability</c> field value.</summary>
[JsonConverter(typeof(AvailabilityJsonConverter))]
public enum Availability
{
    /// <summary>Unknown or unrecognised availability value.</summary>
    Unknown,

    /// <summary>The video is private to the uploader.</summary>
    Private,

    /// <summary>Available only to premium subscribers.</summary>
    PremiumOnly,

    /// <summary>Available only to channel subscribers.</summary>
    SubscriberOnly,

    /// <summary>Available only after authentication.</summary>
    NeedsAuth,

    /// <summary>Unlisted.</summary>
    Unlisted,

    /// <summary>Publicly available.</summary>
    Public
}
