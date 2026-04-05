namespace Shared.Entities.MediaData;

public abstract class MediaStream 
{
    public string CodecName { get; set; } = null!;
    public string CodecLongName { get; set; } = null!;
    public long BitRate { get; set; }
    public int? BitDepth { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string? Language { get; set; }
}