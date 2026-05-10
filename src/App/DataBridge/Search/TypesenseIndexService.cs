using Microsoft.Extensions.Logging;
using Typesense;

namespace DataBridge.Search;

public sealed class TypesenseIndexService(
    ITypesenseClient client,
    ILogger<TypesenseIndexService> logger) : ITypesenseIndexService
{
    private const int ImportBatchSize = 500;

    public async Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default)
    {
        var createdAny = false;
        createdAny |= await EnsureCollectionAsync(MediaCollectionSchema.CollectionName, MediaCollectionSchema.Build(), ct);
        createdAny |= await EnsureCollectionAsync(CommentsCollectionSchema.CollectionName, CommentsCollectionSchema.Build(), ct);
        createdAny |= await EnsureCollectionAsync(CaptionsCollectionSchema.CollectionName, CaptionsCollectionSchema.Build(), ct);
        return createdAny;
    }

    public async Task RecreateAllCollectionsAsync(CancellationToken ct = default)
    {
        await RecreateCollectionAsync(MediaCollectionSchema.CollectionName, MediaCollectionSchema.Build(), ct);
        await RecreateCollectionAsync(CommentsCollectionSchema.CollectionName, CommentsCollectionSchema.Build(), ct);
        await RecreateCollectionAsync(CaptionsCollectionSchema.CollectionName, CaptionsCollectionSchema.Build(), ct);
    }

    public async Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default)
    {
        var response = await client.RetrieveCollection(collection, ct);
        return response.NumberOfDocuments;
    }

    public async Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await client.UpsertDocument(MediaCollectionSchema.CollectionName, document);
    }

    public Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default)
        => ImportAsync(MediaCollectionSchema.CollectionName, documents, ct);

    public Task DeleteCommentsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default)
        => DeleteByMediaGuidAsync(CommentsCollectionSchema.CollectionName, mediaGuid, ct);

    public Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default)
        => ImportAsync(CommentsCollectionSchema.CollectionName, documents, ct);

    public Task DeleteCaptionsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default)
        => DeleteByMediaGuidAsync(CaptionsCollectionSchema.CollectionName, mediaGuid, ct);

    public Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default)
        => ImportAsync(CaptionsCollectionSchema.CollectionName, documents, ct);

    private async Task<bool> EnsureCollectionAsync(string name, Schema schema, CancellationToken ct)
    {
        try
        {
            await client.RetrieveCollection(name, ct);
            logger.LogInformation("Typesense collection {Collection} already exists.", name);
            return false;
        }
        catch (TypesenseApiNotFoundException)
        {
            await client.CreateCollection(schema);
            logger.LogInformation("Created Typesense collection {Collection}.", name);
            return true;
        }
    }

    private async Task RecreateCollectionAsync(string name, Schema schema, CancellationToken ct)
    {
        try
        {
            await client.DeleteCollection(name, compactStore: false);
        }
        catch (TypesenseApiNotFoundException)
        {
            // Expected when the index has never been built.
        }

        ct.ThrowIfCancellationRequested();
        await client.CreateCollection(schema);
        logger.LogInformation("Recreated Typesense collection {Collection}.", name);
    }

    private async Task DeleteByMediaGuidAsync(string collection, string mediaGuid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await client.DeleteDocuments(collection, TypesenseSearchHelpers.Eq("media_guid", mediaGuid), ImportBatchSize);
    }

    private async Task ImportAsync<T>(string collection, IReadOnlyList<T> documents, CancellationToken ct)
        where T : class
    {
        if (documents.Count == 0)
            return;

        ct.ThrowIfCancellationRequested();
        var results = await client.ImportDocuments(collection, documents, ImportBatchSize, ImportType.Upsert);
        var failures = results.Where(x => !x.Success).Take(5).ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Typesense import into '{collection}' failed for {failures.Length} document(s). First error: {failures[0].Error}");
        }
    }
}
