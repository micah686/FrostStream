namespace Shared.Entities.MediaData;

public abstract class ChapterData(string title, TimeSpan start, TimeSpan end)
{
    public string Title { get; private set; } = title;
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;

    public TimeSpan Duration => End - Start;
}