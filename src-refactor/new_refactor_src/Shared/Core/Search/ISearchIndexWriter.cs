namespace DataBridge.Search;

public interface ISearchIndexWriter
{
    Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default);

    Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default);

    /// <summary>True when the live <c>media</c> collection already declares <paramref name="fieldName"/>.</summary>
    Task<bool> MediaCollectionHasFieldAsync(string fieldName, CancellationToken ct = default);

    Task<ISearchGeneration> CreateGenerationAsync(CancellationToken ct = default);

    Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default);

    Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default);

    Task DeleteMediaByGuidAsync(string mediaGuid, CancellationToken ct = default);

    Task DeleteCommentsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default);

    Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default);

    Task DeleteCaptionsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default);

    Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default);
}

/// <summary>A disposable replacement. Activation preserves the previous usable generation.</summary>
public interface ISearchGeneration : IAsyncDisposable
{
    ISearchIndexWriter Writer { get; }
    Task ActivateAsync(CancellationToken ct = default);
}
