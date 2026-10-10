namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Identifies which external binary a provisioning operation is targeting.
/// </summary>
public enum BinaryKind
{
    /// <summary>The yt-dlp executable.</summary>
    YtDlp,

    /// <summary>The ffmpeg executable.</summary>
    Ffmpeg,

    /// <summary>The ffprobe executable.</summary>
    Ffprobe,

    /// <summary>The Deno JavaScript runtime executable.</summary>
    Deno,

    /// <summary>The bgutil-ytdlp-pot-provider yt-dlp plugin package (a zip of a yt_dlp_plugins tree).</summary>
    BgUtilPlugin
}
