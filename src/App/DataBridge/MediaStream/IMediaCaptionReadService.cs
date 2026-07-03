using Shared.Messaging;

namespace DataBridge.MediaStream;

public interface IMediaCaptionReadService
{
    Task<MediaCaptionLocationDto?> ResolveAsync(
        Guid mediaGuid,
        string languageCode,
        string? captionType,
        CancellationToken cancellationToken = default);
}
