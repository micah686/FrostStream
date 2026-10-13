namespace YtDlpSharpLib.Options;

/// <summary>
/// Container formats accepted by yt-dlp's <c>--remux-video</c> option.
/// </summary>
public enum VideoContainer
{
    /// <summary>AAC audio container.</summary>
    [YtDlpEnumValue("aac")]
    Aac,

    /// <summary>AIFF audio container.</summary>
    [YtDlpEnumValue("aiff")]
    Aiff,

    /// <summary>Apple Lossless Audio Codec container.</summary>
    [YtDlpEnumValue("alac")]
    Alac,

    /// <summary>AVI container.</summary>
    [YtDlpEnumValue("avi")]
    Avi,

    /// <summary>FLAC audio container.</summary>
    [YtDlpEnumValue("flac")]
    Flac,

    /// <summary>FLV container.</summary>
    [YtDlpEnumValue("flv")]
    Flv,

    /// <summary>GIF container.</summary>
    [YtDlpEnumValue("gif")]
    Gif,

    /// <summary>MPEG-4 audio container.</summary>
    [YtDlpEnumValue("m4a")]
    M4a,

    /// <summary>Matroska audio container.</summary>
    [YtDlpEnumValue("mka")]
    Mka,

    /// <summary>Matroska video container.</summary>
    [YtDlpEnumValue("mkv")]
    Mkv,

    /// <summary>QuickTime/MOV container.</summary>
    [YtDlpEnumValue("mov")]
    Mov,

    /// <summary>MP3 audio container.</summary>
    [YtDlpEnumValue("mp3")]
    Mp3,

    /// <summary>MPEG-4 video container.</summary>
    [YtDlpEnumValue("mp4")]
    Mp4,

    /// <summary>Ogg container.</summary>
    [YtDlpEnumValue("ogg")]
    Ogg,

    /// <summary>Opus audio container.</summary>
    [YtDlpEnumValue("opus")]
    Opus,

    /// <summary>Vorbis audio container.</summary>
    [YtDlpEnumValue("vorbis")]
    Vorbis,

    /// <summary>Waveform audio container.</summary>
    [YtDlpEnumValue("wav")]
    Wav,

    /// <summary>WebM container.</summary>
    [YtDlpEnumValue("webm")]
    Webm
}
