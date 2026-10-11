namespace DataBridge.Search;

public interface IMediaDocumentQuery
{
    Task<MediaDocument?> GetMediaByGuidAsync(Guid mediaGuid, CancellationToken ct = default);

    Task<IReadOnlyList<MediaDocument>> GetMediaByGuidsAsync(IReadOnlyCollection<Guid> mediaGuids, CancellationToken ct = default);

    Task<IReadOnlyList<CommentDocument>> GetCommentsByMediaGuidAsync(Guid mediaGuid, CancellationToken ct = default);

    Task<IReadOnlyList<CaptionDocument>> GetCaptionsByMediaGuidAsync(Guid mediaGuid, CancellationToken ct = default);

    Task<DocumentBatch<MediaDocument>> GetMediaBatchAsync(long lastId, int pageSize, CancellationToken ct = default);

    Task<DocumentBatch<CommentDocument>> GetCommentBatchAsync(long lastId, int pageSize, CancellationToken ct = default);

    Task<DocumentBatch<CaptionDocument>> GetCaptionBatchAsync(long lastId, int pageSize, CancellationToken ct = default);
}

public sealed record DocumentBatch<T>(IReadOnlyList<T> Documents, long LastId);
