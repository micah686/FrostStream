using Shared.Messaging;

namespace DataBridge.MediaStream;

public interface IMediaCaptionReadService
{
    Task<IReadOnlyList<MediaCaptionLocationDto>> ListAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken = default);

    Task<MediaCaptionLocationDto?> ResolveAsync(
        Guid mediaGuid,
        string languageCode,
        string? captionType,
        CancellationToken cancellationToken = default);
}
