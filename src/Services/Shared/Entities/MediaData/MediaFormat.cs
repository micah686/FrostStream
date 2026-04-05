namespace Shared.Entities.MediaData;

public abstract class MediaFormat
{
    public TimeSpan Duration { get; set; }
    public TimeSpan StartTime { get; set; }
    public string[] FormatLongNames { get; set; } = null!; //split on '/'
    public int StreamCount { get; set; }
    public double BitRate { get; set; }
}