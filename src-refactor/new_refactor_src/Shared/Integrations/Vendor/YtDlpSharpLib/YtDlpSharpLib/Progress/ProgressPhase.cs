namespace YtDlpSharpLib.Progress;

/// <summary>
/// The high-level lifecycle phase observed in a yt-dlp progress line.
/// </summary>
public enum ProgressPhase
{
    /// <summary>The line could not be classified to a more specific phase.</summary>
    Unknown,

    /// <summary>The video stream is being downloaded.</summary>
    Downloading,

    /// <summary>ffmpeg is merging downloaded streams.</summary>
    Merging,

    /// <summary>ffmpeg is extracting audio from a downloaded stream.</summary>
    ExtractingAudio,

    /// <summary>The downloaded media is being converted to another format.</summary>
    Converting,

    /// <summary>A thumbnail is being embedded into the final file.</summary>
    EmbeddingThumbnail,

    /// <summary>A general yt-dlp post-processing step.</summary>
    PostProcessing,

    /// <summary>The current item has finished downloading (100% or already-on-disk).</summary>
    Finished,

    /// <summary>The download completed successfully.</summary>
    Completed
}
