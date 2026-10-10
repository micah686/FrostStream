namespace YtDlpSharpLib.Scheduling;

/// <summary>The kind of download a <see cref="DownloadRequest"/> represents.</summary>
public enum DownloadRequestKind
{
    /// <summary>Standard video download.</summary>
    Video,

    /// <summary>Audio-only download.</summary>
    Audio,

    /// <summary>Playlist download.</summary>
    Playlist,

    /// <summary>Audio-only playlist download.</summary>
    AudioPlaylist,

    /// <summary>Metadata-only download (info-json + optional sidecars).</summary>
    Metadata,

    /// <summary>Live-chat replay download.</summary>
    LiveChat
}
