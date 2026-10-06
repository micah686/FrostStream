using Shared.Messaging;

namespace DataBridge.MediaStream;

public interface IMediaStreamReadService
{
    Task<MediaStreamLocationDto?> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? version,
        CancellationToken cancellationToken = default);
}
