namespace Shared.Entities.MediaData;

public abstract class VideoStream
{
    public double AvgFrameRate { get; set; } //uses avg_frame_rate
    
    public int BitsPerRawSample { get; set; }
    public (int Width, int Height) DisplayAspectRatio { get; set; }
    public string Profile { get; set; } = null!;
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixelFormat { get; set; } = null!;
    public int Rotation { get; set; }
    public string ColorSpace { get; set; } = null!;
    public string ColorTransfer { get; set; } = null!;
    public string ColorPrimaries { get; set; } = null!;

    public string Resolution => Math.Max(Width, Height) switch
    {
        <= 480 => "480p (SD)",
        <= 720 => "720p (HD)",
        <= 1080 => "1080p (Full HD)",
        <= 1440 => "1440p (2K/QHD)",
        <= 2160 => "4K (UHD)",
        <= 4320 => "8K",
        _ => $"{Math.Max(Width, Height)}p"
    };
    public HDRType HDRType { get; set; }
}

public enum HDRType
{
    SDR,
    HDR,
    HDR10,
    Unknown
}