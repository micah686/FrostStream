namespace YtDlpSharpLib.Options;

/// <summary>
/// Container formats accepted by yt-dlp's <c>--merge-output-format</c> option.
/// </summary>
public enum DownloadMergeFormat
{
    /// <summary>AVI container.</summary>
    [YtDlpEnumValue("avi")]
    Avi,

    /// <summary>FLV container.</summary>
    [YtDlpEnumValue("flv")]
    Flv,

    /// <summary>Matroska container.</summary>
    [YtDlpEnumValue("mkv")]
    Mkv,

    /// <summary>QuickTime/MOV container.</summary>
    [YtDlpEnumValue("mov")]
    Mov,

    /// <summary>MPEG-4 container.</summary>
    [YtDlpEnumValue("mp4")]
    Mp4,

    /// <summary>WebM container.</summary>
    [YtDlpEnumValue("webm")]
    Webm
}
