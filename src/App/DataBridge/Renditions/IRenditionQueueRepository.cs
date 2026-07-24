using Shared.Messaging;

namespace DataBridge.Renditions;

public interface IRenditionQueueRepository
{
    Task<RenditionQueuePage> QueryAsync(RenditionQueueListRequest request, CancellationToken cancellationToken = default);
}

public sealed record RenditionQueuePage(IReadOnlyList<RenditionQueueItemDto> Items, string? NextCursor, int TotalCount);
