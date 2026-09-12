namespace MediaProcessor;

public sealed class MediaProcessorOptions
{
    public const string SectionName = "MediaProcessor";

    public string WebApiBaseUrl { get; init; } = "http://localhost:25200";

    public string? ApiKey { get; init; }

    public string FfmpegPath { get; init; } = "ffmpeg";

    public string FfprobePath { get; init; } = "ffprobe";

    public string TempRoot { get; init; } = Path.Combine(Path.GetTempPath(), "froststream", "mediaprocessor");

    public string AacBitrate { get; init; } = "128k";

    public string OpusBitrate { get; init; } = "96k";

    public string Mp3Bitrate { get; init; } = "128k";

    public int HlsSegmentSeconds { get; init; } = 6;

    /// <summary>x264 CRF used when the source video track must be transcoded to H.264.</summary>
    public int VideoCrf { get; init; } = 21;

    public string VideoPreset { get; init; } = "veryfast";

    /// <summary>Frame height cap for transcoded video; sources at or below pass through unscaled.</summary>
    public int VideoMaxHeight { get; init; } = 1080;
}
