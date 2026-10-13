using Microsoft.Extensions.Logging;
using Typesense;

namespace DataBridge.Search;

public sealed class TypesenseIndexService(
    ITypesenseClient client,
    ILogger<TypesenseIndexService> logger, TypesenseGenerationRouter router) : ISearchIndexWriter
{
    private const int ImportBatchSize = 500;

    private string? _generation;
    private string Name(string collection) => _generation is null ? router.Name(collection) : collection + "_" + _generation;

    public async Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default)
    {
        if (await router.LoadAsync(ct)) return false;
        await using var generation = await CreateGenerationAsync(ct);
        await generation.ActivateAsync(ct);
        return true;
    }

    public async Task<ISearchGeneration> CreateGenerationAsync(CancellationToken ct = default)
    {
        var writer = new TypesenseIndexService(client, logger, router) { _generation = Guid.NewGuid().ToString("N") };
        var generation = new Generation(writer, client, router, logger);
        try
        {
            await client.CreateCollection(MediaCollectionSchema.Build(writer.Name("media")));
            ct.ThrowIfCancellationRequested();
            await client.CreateCollection(CommentsCollectionSchema.Build(writer.Name("comments")));
            ct.ThrowIfCancellationRequested();
            await client.CreateCollection(CaptionsCollectionSchema.Build(writer.Name("captions")));
            return generation;
        }
        catch { await generation.DisposeAsync(); throw; }
    }

    private sealed class Generation(TypesenseIndexService writer, ITypesenseClient client, TypesenseGenerationRouter router, ILogger logger) : ISearchGeneration
    {
        private bool _activated;
        public ISearchIndexWriter Writer => writer;
        public async Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Validate every collection before the single persistent activation point.
            foreach (var name in new[] { "media", "comments", "captions" })
                await client.RetrieveCollection(writer.Name(name), ct);
            try
            {
                await client.UpsertCollectionAlias(TypesenseGenerationRouter.Alias, new CollectionAlias(writer.Name("media")));
            }
            catch
            {
                // The server may have committed activation before the response was lost.
                var alias = await client.RetrieveCollectionAlias(TypesenseGenerationRouter.Alias, CancellationToken.None);
                if (alias.CollectionName != writer.Name("media")) throw;
            }
            _activated = true;
            router.Activate(writer._generation!);
        }
        public async ValueTask DisposeAsync()
        {
            if (_activated) return; // Retain active and previous generations for recovery.
            try
            {
                var alias = await client.RetrieveCollectionAlias(TypesenseGenerationRouter.Alias, CancellationToken.None);
                if (alias.CollectionName == writer.Name("media")) { router.Activate(writer._generation!); return; }
            }
            catch (TypesenseApiNotFoundException) { }
            catch (Exception ex)
            {
                // An uncertain activation must never cause deletion of a potentially active index.
                logger.LogWarning(ex, "Retaining search generation because its activation status is unknown.");
                return;
            }
            foreach (var name in new[] { "media", "comments", "captions" })
            {
                try { await client.DeleteCollection(writer.Name(name), compactStore: false); }
                catch (TypesenseApiNotFoundException) { }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove incomplete search generation {Generation}", writer._generation); }
            }
        }
    }

    public async Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default)
    {
        var response = await client.RetrieveCollection(Name(collection), ct);
        return response.NumberOfDocuments;
    }

    public async Task<bool> MediaCollectionHasFieldAsync(string fieldName, CancellationToken ct = default)
    {
        try
        {
            var response = await client.RetrieveCollection(Name(MediaCollectionSchema.CollectionName), ct);
            return response.Fields.Any(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));
        }
        catch (TypesenseApiNotFoundException)
        {
            return false;
        }
    }

    public async Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await client.UpsertDocument(Name(MediaCollectionSchema.CollectionName), document);
    }

    public Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default)
        => ImportAsync(MediaCollectionSchema.CollectionName, documents, ct);

    public async Task DeleteMediaByGuidAsync(string mediaGuid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // The media collection's document id is the media_guid (see MediaDocument.Id).
            await client.DeleteDocument<MediaDocument>(Name(MediaCollectionSchema.CollectionName), mediaGuid);
        }
        catch (TypesenseApiNotFoundException)
        {
            // Already absent from the index — nothing to delete.
        }
    }

    public Task DeleteCommentsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default)
        => DeleteByMediaGuidAsync(CommentsCollectionSchema.CollectionName, mediaGuid, ct);

    public Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default)
        => ImportAsync(CommentsCollectionSchema.CollectionName, documents, ct);

    public Task DeleteCaptionsByMediaGuidAsync(string mediaGuid, CancellationToken ct = default)
        => DeleteByMediaGuidAsync(CaptionsCollectionSchema.CollectionName, mediaGuid, ct);

    public Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default)
        => ImportAsync(CaptionsCollectionSchema.CollectionName, documents, ct);

    private async Task DeleteByMediaGuidAsync(string collection, string mediaGuid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await client.DeleteDocuments(Name(collection), TypesenseSearchReader.Translate(new SearchFilter.Text("media_guid", mediaGuid))!, ImportBatchSize);
    }

    private async Task ImportAsync<T>(string collection, IReadOnlyList<T> documents, CancellationToken ct)
        where T : class
    {
        if (documents.Count == 0)
            return;

        ct.ThrowIfCancellationRequested();
        var results = await client.ImportDocuments(Name(collection), documents, ImportBatchSize, ImportType.Upsert);
        var failures = results.Where(x => !x.Success).Take(5).ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Typesense import into '{collection}' failed for {failures.Length} document(s). First error: {failures[0].Error}");
        }
    }
}
