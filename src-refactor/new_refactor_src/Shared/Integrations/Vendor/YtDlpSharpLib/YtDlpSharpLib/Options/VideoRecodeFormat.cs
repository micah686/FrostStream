namespace YtDlpSharpLib.Options;

/// <summary>
/// Formats accepted by yt-dlp's <c>--recode-video</c> option.
/// </summary>
public enum VideoRecodeFormat
{
    /// <summary>AAC audio.</summary>
    [YtDlpEnumValue("aac")]
    Aac,

    /// <summary>AIFF audio.</summary>
    [YtDlpEnumValue("aiff")]
    Aiff,

    /// <summary>Apple Lossless Audio Codec.</summary>
    [YtDlpEnumValue("alac")]
    Alac,

    /// <summary>AVI video.</summary>
    [YtDlpEnumValue("avi")]
    Avi,

    /// <summary>FLAC audio.</summary>
    [YtDlpEnumValue("flac")]
    Flac,

    /// <summary>FLV video.</summary>
    [YtDlpEnumValue("flv")]
    Flv,

    /// <summary>GIF video.</summary>
    [YtDlpEnumValue("gif")]
    Gif,

    /// <summary>MPEG-4 audio.</summary>
    [YtDlpEnumValue("m4a")]
    M4a,

    /// <summary>Matroska audio.</summary>
    [YtDlpEnumValue("mka")]
    Mka,

    /// <summary>Matroska video.</summary>
    [YtDlpEnumValue("mkv")]
    Mkv,

    /// <summary>QuickTime/MOV video.</summary>
    [YtDlpEnumValue("mov")]
    Mov,

    /// <summary>MP3 audio.</summary>
    [YtDlpEnumValue("mp3")]
    Mp3,

    /// <summary>MPEG-4 video.</summary>
    [YtDlpEnumValue("mp4")]
    Mp4,

    /// <summary>Ogg audio.</summary>
    [YtDlpEnumValue("ogg")]
    Ogg,

    /// <summary>Opus audio.</summary>
    [YtDlpEnumValue("opus")]
    Opus,

    /// <summary>Vorbis audio.</summary>
    [YtDlpEnumValue("vorbis")]
    Vorbis,

    /// <summary>Waveform audio.</summary>
    [YtDlpEnumValue("wav")]
    Wav,

    /// <summary>WebM video.</summary>
    [YtDlpEnumValue("webm")]
    Webm
}
