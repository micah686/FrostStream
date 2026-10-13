using System.Runtime.InteropServices;
using System.Text.Json;
using DataBridge.Search;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Microsoft.Extensions.Logging;

namespace FrostStream.Lite.Infrastructure.Search;

/// <summary>One owned writer per filesystem generation; shared readers observe only durable commits.</summary>
public sealed class LuceneSearchService(LuceneSearchOptions options, ILogger<LuceneSearchService> logger)
    : ISearchReader, ISearchIndexWriter, IDisposable
{
    public const int SchemaVersion = 1;
    private readonly ReaderWriterLockSlim lifetime = new();
    private LuceneIndexState? active;
    private FileStream? owner;
    private bool initialized;
    private bool disposed;
    private string ManifestPath => Path.Combine(options.DirectoryPath, "active.json");
    private sealed record Manifest(int Schema, string Generation);

    public Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lifetime.EnterWriteLock();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return Task.FromResult(active is null);
            System.IO.Directory.CreateDirectory(options.DirectoryPath);
            // The root lock also excludes a second process building a different generation.
            owner = new FileStream(Path.Combine(options.DirectoryPath, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            initialized = true;
            try
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
                if (manifest is null || manifest.Schema != SchemaVersion || !Guid.TryParseExact(manifest.Generation, "N", out _))
                    throw new InvalidDataException("Incompatible Lucene search manifest.");
                active = new LuceneIndexState(GenerationPath(manifest.Generation), manifest.Generation, options.RamBufferMiB, true) { Activated = true };
                return Task.FromResult(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or Lucene.Net.Index.CorruptIndexException or Lucene.Net.Index.IndexFormatTooOldException or Lucene.Net.Index.IndexFormatTooNewException)
            {
                logger.LogWarning(ex, "Search index is missing, corrupt, or incompatible; an authoritative rebuild is required.");
                return Task.FromResult(true);
            }
        }
        finally { lifetime.ExitWriteLock(); }
    }
    private string GenerationPath(string generation) => Path.Combine(options.DirectoryPath, "g-" + generation);
    public Task<ISearchGeneration> CreateGenerationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lifetime.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized) throw new InvalidOperationException("Search must be initialized before rebuilding.");
            var id = Guid.NewGuid().ToString("N");
            var state = new LuceneIndexState(GenerationPath(id), id, options.RamBufferMiB, false);
            return Task.FromResult<ISearchGeneration>(new Generation(this, state));
        }
        finally { lifetime.ExitReadLock(); }
    }
    public Task<SearchPage<T>> SearchAsync<T>(SearchCollection collection, SearchRequest request, CancellationToken ct = default) where T : class
    {
        ct.ThrowIfCancellationRequested();
        lifetime.EnterReadLock();
        try
        {
            var state = Active();
            var page = Math.Max(1, request.Page); var size = Math.Clamp(request.PerPage, 1, 250);
            var depth = (long)page * size;
            if (depth > options.MaximumPageDepth) throw new ArgumentOutOfRangeException(nameof(request), "Search page depth exceeds the configured bound.");
            var searcher = state.Searchers.Acquire();
            try
            {
                var result = searcher.Search(LuceneDocumentCodec.Query(collection, request, state.Analyzer), (int)depth, LuceneDocumentCodec.Sort(request));
                var hits = result.ScoreDocs.Skip((page - 1) * size).Select(hit =>
                {
                    ct.ThrowIfCancellationRequested();
                    return new SearchDocumentHit<T>(JsonSerializer.Deserialize<T>(searcher.Doc(hit.Doc).Get("_json"))
                        ?? throw new InvalidDataException("Search document cannot be decoded."));
                }).ToArray();
                return Task.FromResult(new SearchPage<T>(hits, result.TotalHits));
            }
            finally { state.Searchers.Release(searcher); }
        }
        finally { lifetime.ExitReadLock(); }
    }
    private LuceneIndexState Active()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return active ?? throw new InvalidOperationException("Search is building; no valid index is available yet.");
    }
    public Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default)
    {
        var parsed = Enum.Parse<SearchCollection>(collection, true);
        return CountAsync(parsed, ct);
    }
    private async Task<int> CountAsync(SearchCollection collection, CancellationToken ct)
        => (await SearchAsync<JsonElementDocument>(collection, new SearchRequest("*", "id") { PerPage = 1 }, ct)).Found;
    private sealed record JsonElementDocument;
    public Task<bool> MediaCollectionHasFieldAsync(string fieldName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(active is not null && fieldName is "sort_id" or "added_at_sort");
    }
    private Task Mutate(Action<LuceneIndexState> action, CancellationToken ct, LuceneIndexState? target = null)
    {
        ct.ThrowIfCancellationRequested(); lifetime.EnterReadLock();
        try
        {
            var state = target ?? Active();
            lock (state.WriteGate)
            {
                ct.ThrowIfCancellationRequested(); action(state);
                if (state.Activated) state.Commit();
            }
            return Task.CompletedTask;
        }
        finally { lifetime.ExitReadLock(); }
    }
    private Task Import<T>(SearchCollection collection, IReadOnlyList<T> documents, CancellationToken ct, LuceneIndexState? target = null)
    {
        if (documents.Count > 500) throw new ArgumentException("Index batches must contain at most 500 documents.");
        return Mutate(state =>
        {
            foreach (var item in documents)
            {
                ct.ThrowIfCancellationRequested(); var document = LuceneDocumentCodec.Encode(collection, item);
                state.Writer.UpdateDocument(new Term("_key", document.GetField("_key").GetStringValue()), document);
            }
        }, ct, target);
    }
    private Task Delete(SearchCollection collection, string id, bool related, CancellationToken ct, LuceneIndexState? target = null)
        => Mutate(state => state.Writer.DeleteDocuments(new BooleanQuery
        {
            { new TermQuery(new Term("_collection", LuceneDocumentCodec.Collection(collection))), Occur.MUST },
            { new TermQuery(new Term("e_" + (related ? "media_guid" : "id"), LuceneDocumentCodec.Normalize(id))), Occur.MUST }
        }), ct, target);
    public Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default) => Import(SearchCollection.Media, new[] { document }, ct);
    public Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default) => Import(SearchCollection.Media, documents, ct);
    public Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default) => Import(SearchCollection.Comments, documents, ct);
    public Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default) => Import(SearchCollection.Captions, documents, ct);
    public Task DeleteMediaByGuidAsync(string id, CancellationToken ct = default) => Delete(SearchCollection.Media, id, false, ct);
    public Task DeleteCommentsByMediaGuidAsync(string id, CancellationToken ct = default) => Delete(SearchCollection.Comments, id, true, ct);
    public Task DeleteCaptionsByMediaGuidAsync(string id, CancellationToken ct = default) => Delete(SearchCollection.Captions, id, true, ct);

    private void Activate(LuceneIndexState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lifetime.EnterWriteLock();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (state.WriteGate) state.Commit();
            var temporary = ManifestPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Manifest(SchemaVersion, state.Generation)); stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, ManifestPath, true);
            // After rename, this generation must remain owned even if directory fsync fails.
            var previous = active; active = state; state.Activated = true;
            previous?.Dispose();
            SyncDirectory(options.DirectoryPath);
            Prune(previous?.Generation, state.Generation);
        }
        finally { lifetime.ExitWriteLock(); }
    }
    private void Prune(string? previous, string current)
    {
        foreach (var path in System.IO.Directory.EnumerateDirectories(options.DirectoryPath, "g-*"))
        {
            var generation = Path.GetFileName(path)[2..];
            if (generation == previous || generation == current || !Guid.TryParseExact(generation, "N", out _)) continue;
            try { System.IO.Directory.Delete(path, true); }
            catch (IOException ex) { logger.LogWarning(ex, "Could not prune obsolete search generation {Generation}.", generation); }
        }
    }
    private static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fd = Native.open(path, 0x10000); // O_DIRECTORY; directory entries need fsync after atomic rename.
        if (fd < 0) throw new IOException("Could not open search directory for durable activation.");
        try { if (Native.fsync(fd) != 0) throw new IOException("Could not persist search generation activation."); }
        finally { Native.close(fd); }
    }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
    }
    public void Dispose()
    {
        lifetime.EnterWriteLock();
        try { if (disposed) return; disposed = true; active?.Dispose(); active = null; owner?.Dispose(); }
        finally { lifetime.ExitWriteLock(); }
    }
    private sealed class Generation(LuceneSearchService service, LuceneIndexState state) : ISearchGeneration, ISearchIndexWriter
    {
        public ISearchIndexWriter Writer => this;
        public Task ActivateAsync(CancellationToken ct = default) { service.Activate(state, ct); return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            if (!state.Activated) { state.Dispose(); System.IO.Directory.Delete(service.GenerationPath(state.Generation), true); }
            return ValueTask.CompletedTask;
        }
        public Task UpsertMediaAsync(MediaDocument document, CancellationToken ct = default) => service.Import(SearchCollection.Media, new[] { document }, ct, state);
        public Task BulkImportMediaAsync(IReadOnlyList<MediaDocument> documents, CancellationToken ct = default) => service.Import(SearchCollection.Media, documents, ct, state);
        public Task BulkImportCommentsAsync(IReadOnlyList<CommentDocument> documents, CancellationToken ct = default) => service.Import(SearchCollection.Comments, documents, ct, state);
        public Task BulkImportCaptionsAsync(IReadOnlyList<CaptionDocument> documents, CancellationToken ct = default) => service.Import(SearchCollection.Captions, documents, ct, state);
        public Task DeleteMediaByGuidAsync(string id, CancellationToken ct = default) => service.Delete(SearchCollection.Media, id, false, ct, state);
        public Task DeleteCommentsByMediaGuidAsync(string id, CancellationToken ct = default) => service.Delete(SearchCollection.Comments, id, true, ct, state);
        public Task DeleteCaptionsByMediaGuidAsync(string id, CancellationToken ct = default) => service.Delete(SearchCollection.Captions, id, true, ct, state);
        public Task<bool> EnsureAllCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetDocumentCountAsync(string collection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MediaCollectionHasFieldAsync(string fieldName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ISearchGeneration> CreateGenerationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
