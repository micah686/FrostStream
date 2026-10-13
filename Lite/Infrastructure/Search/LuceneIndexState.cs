using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace FrostStream.Lite.Infrastructure.Search;

internal sealed class LuceneIndexState : IDisposable
{
    public string Generation { get; }
    public FSDirectory Directory { get; }
    public StandardAnalyzer Analyzer { get; }
    public IndexWriter Writer { get; }
    public SearcherManager Searchers { get; }
    public object WriteGate { get; } = new();
    public bool Activated { get; set; }
    public LuceneIndexState(string path, string generation, double ramBufferMiB, bool existing)
    {
        Generation = generation;
        Directory = FSDirectory.Open(path);
        StandardAnalyzer? analyzer = null;
        IndexWriter? writer = null;
        try
        {
            if (existing)
            {
                var check = new CheckIndex(Directory);
                if (!check.DoCheckIndex().Clean) throw new InvalidDataException("Lucene index checksum validation failed.");
            }
            Analyzer = analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
            var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, Analyzer)
            {
                OpenMode = existing ? OpenMode.APPEND : OpenMode.CREATE,
                RAMBufferSizeMB = ramBufferMiB,
                MergeScheduler = new SerialMergeScheduler()
            };
            Writer = writer = new IndexWriter(Directory, config);
            if (!existing) Writer.Commit();
            Searchers = new SearcherManager(Writer, true, null);
        }
        catch { writer?.Rollback(); analyzer?.Dispose(); Directory.Dispose(); throw; }
    }
    public void Commit()
    {
        Writer.Commit(); // fsync completes before pending-index revision acknowledgment.
        Searchers.MaybeRefreshBlocking();
    }
    public void Dispose()
    {
        try { Searchers.Dispose(); }
        finally
        {
            try { Writer.Rollback(); } // Every successful active write already committed; discard failed/unpublished work.
            finally { Analyzer.Dispose(); Directory.Dispose(); }
        }
    }
}
