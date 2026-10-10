namespace YtDlpSharpLib.Options;

/// <summary>
/// Subtitle formats accepted by yt-dlp subtitle format and conversion options.
/// </summary>
public enum SubtitleFormat
{
    /// <summary>Disable subtitle conversion where supported.</summary>
    [YtDlpEnumValue("none")]
    None,

    /// <summary>Best available subtitle format selected by yt-dlp.</summary>
    [YtDlpEnumValue("best")]
    Best,

    /// <summary>Advanced SubStation Alpha subtitles.</summary>
    [YtDlpEnumValue("ass")]
    Ass,

    /// <summary>LyRiCs subtitle format.</summary>
    [YtDlpEnumValue("lrc")]
    Lrc,

    /// <summary>SubRip subtitles.</summary>
    [YtDlpEnumValue("srt")]
    Srt,

    /// <summary>WebVTT subtitles.</summary>
    [YtDlpEnumValue("vtt")]
    Vtt
}
