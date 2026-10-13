using Shared.Messaging;

namespace DataBridge.MediaStream;

public interface IMediaThumbnailReadService
{
    Task<MediaThumbnailLocationDto?> ResolveAsync(
        Guid mediaGuid,
        CancellationToken cancellationToken = default);
}
