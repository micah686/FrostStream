namespace YtDlpSharpLib.Options;

/// <summary>
/// Audio conversion formats accepted by yt-dlp's <c>--audio-format</c> option.
/// </summary>
public enum AudioConversionFormat
{
    /// <summary>Let yt-dlp choose the best available audio output format.</summary>
    [YtDlpEnumValue("best")]
    Best,

    /// <summary>AAC audio.</summary>
    [YtDlpEnumValue("aac")]
    Aac,

    /// <summary>Apple Lossless Audio Codec.</summary>
    [YtDlpEnumValue("alac")]
    Alac,

    /// <summary>FLAC lossless audio.</summary>
    [YtDlpEnumValue("flac")]
    Flac,

    /// <summary>MPEG-4 audio container.</summary>
    [YtDlpEnumValue("m4a")]
    M4a,

    /// <summary>MP3 audio.</summary>
    [YtDlpEnumValue("mp3")]
    Mp3,

    /// <summary>Opus audio.</summary>
    [YtDlpEnumValue("opus")]
    Opus,

    /// <summary>Vorbis audio.</summary>
    [YtDlpEnumValue("vorbis")]
    Vorbis,

    /// <summary>Waveform audio.</summary>
    [YtDlpEnumValue("wav")]
    Wav
}
