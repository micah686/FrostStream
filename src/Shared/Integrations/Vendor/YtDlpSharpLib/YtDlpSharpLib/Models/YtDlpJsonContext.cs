using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> used to deserialize yt-dlp
/// metadata without reflection, enabling AOT/trim-friendly consumption.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VideoInfo))]
[JsonSerializable(typeof(List<VideoInfo>))]
[JsonSerializable(typeof(FormatInfo))]
[JsonSerializable(typeof(ThumbnailInfo))]
[JsonSerializable(typeof(ChapterInfo))]
[JsonSerializable(typeof(SubtitleTrack))]
[JsonSerializable(typeof(CommentInfo))]
public partial class YtDlpJsonContext : JsonSerializerContext
{
}
