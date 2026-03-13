namespace Shared.Entities;

/// <summary>
/// Represents the type of media content.
/// </summary>
public enum MediaType
{
    Unknown = 0,
    Video = 1,
    Audio = 2
}

/// <summary>
/// Represents the type of video variant.
/// </summary>
public enum VideoVariantType
{
    /// <summary>Original downloaded file from source platform.</summary>
    Original = 0,
    /// <summary>Transcoded variant (e.g., ffmpeg re-encode).</summary>
    Transcoded = 1
}

/// <summary>
/// Represents quality variants for both video (resolution) and audio (bitrate).
/// 
/// For Video content:
///   - Values represent vertical resolution in pixels (480, 720, 1080, 2160, 4320)
///   
/// For Audio content:
///   - Values represent bitrate in kbps (128, 192, 256, 320)
/// </summary>
public enum Quality
{
    Unknown = 0,
    
    // Video resolutions (height in pixels)
    P480 = 480,
    P720 = 720,
    P1080 = 1080,
    P1440 = 1440,
    P4K = 2160,
    P8K = 4320,
    
    // Audio bitrates (in kbps)
    K128 = 1128,  // Offset to avoid collision with video values
    K192 = 1192,
    K256 = 1256,
    K320 = 1320
}
