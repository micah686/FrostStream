using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>Metadata describing a single chapter or section of a video.</summary>
public sealed record ChapterInfo
{
    /// <summary>Chapter title.</summary>
    public string? Title { get; init; }

    /// <summary>Start time in seconds.</summary>
    [JsonPropertyName("start_time")]
    public double? StartTime { get; init; }

    /// <summary>End time in seconds.</summary>
    [JsonPropertyName("end_time")]
    public double? EndTime { get; init; }
}
