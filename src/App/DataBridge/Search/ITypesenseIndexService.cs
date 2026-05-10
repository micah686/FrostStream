namespace DataBridge.Search;

public interface ITypesenseIndexService
{
    Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default);

    Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default);

    Task RecreateAllCollectionsAsync(CancellationToken ct = default);

    Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default);

    Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default);

    Task DeleteCommentsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default);

    Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default);

    Task DeleteCaptionsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default);

    Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default);
}
