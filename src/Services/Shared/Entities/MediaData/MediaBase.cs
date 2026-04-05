
namespace Shared.Entities.MediaData;

public class MediaBase
{
    TimeSpan Duration { get; } = TimeSpan.Zero;
    private MediaFormat? Format { get; } = null;
    List<ChapterData>? Chapters { get; } = null;
    AudioStream? PrimaryAudioStream { get; } = null;
    VideoStream? PrimaryVideoStream { get; } = null;
    MediaStream? PrimarySubtitleStream { get; } = null;
    List<VideoStream>? VideoStreams { get; } = null;
    List<AudioStream>? AudioStreams { get; } = null;
    List<MediaStream>? SubtitleStreams { get; } = null;
}