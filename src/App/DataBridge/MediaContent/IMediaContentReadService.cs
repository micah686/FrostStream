using Shared.Messaging;

namespace DataBridge.MediaContent;

public interface IMediaContentReadService
{
    Task<MediaContentLocationDto?> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? version,
        CancellationToken cancellationToken = default);
}
