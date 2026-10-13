using DataBridge.Persistence.Indexing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataBridge.Search;

/// <summary>Single bounded projection worker per control plane. Database leases coordinate other instances.</summary>
public sealed class PendingIndexWorker(PendingIndexStore store, IMediaDocumentQuery query, ISearchIndexWriter index,
    CaptionDocumentHydrator captions, SearchWriterGate writerGate, ILogger<PendingIndexWorker> logger, Shared.Deployment.ApplicationStartupGate? startup = null, SearchStatus? status = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(startup is not null) await startup.WaitAsync(stoppingToken);
        var idle = TimeSpan.FromMilliseconds(100);
        while (!stoppingToken.IsCancellationRequested)
        {
            IndexWork? work = null;
            try
            {
                await writerGate.Semaphore.WaitAsync(stoppingToken);
                try
                {
                    work = await store.ClaimAsync(stoppingToken);
                    if (work is not null)
                    {
                        idle = TimeSpan.FromMilliseconds(100);
                        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        var renewal = RenewAsync(work, renewalStop.Token);
                        try { await ApplyAsync(work, stoppingToken); await store.CompleteAsync(work, stoppingToken); }
                        finally { renewalStop.Cancel(); try { await renewal; } catch (OperationCanceledException) { } }
                    }
                }
                finally { writerGate.Semaphore.Release(); }
                if (work is null) { await Task.Delay(idle, stoppingToken); idle = TimeSpan.FromMilliseconds(Math.Min(5000, idle.TotalMilliseconds * 2)); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (work is not null) await store.ReleaseAsync(work, CancellationToken.None);
                throw;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                status?.ReportFailure(error.Message);
                logger.LogError(error, "Index intent failed for {MediaGuid}; retained for retry", work?.MediaGuid);
                if (work is not null)
                {
                    if (error is InvalidOperationException && error.Message == "Index lease was lost.") await store.RequestAsync(Guid.Parse(work.MediaGuid), stoppingToken);
                    else await store.FailAsync(work, error, stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
    private async Task RenewAsync(IndexWork work, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct)) await store.RenewAsync(work, ct);
    }
    public async Task ApplyAsync(IndexWork work, CancellationToken ct)
    {
        var id = Guid.Parse(work.MediaGuid);
        var media = await query.GetMediaByGuidAsync(id, ct);
        // Missing source is a tombstone, including every related collection.
        if (media is null)
        {
            await index.DeleteMediaByGuidAsync(work.MediaGuid, ct);
            await index.DeleteCommentsByMediaGuidAsync(work.MediaGuid, ct);
            await index.DeleteCaptionsByMediaGuidAsync(work.MediaGuid, ct);
            return;
        }
        await index.UpsertMediaAsync(media, ct);
        await index.DeleteCommentsByMediaGuidAsync(work.MediaGuid, ct);
        long commentCursor = 0;
        while (true)
        {
            var batch = await query.GetCommentsForMediaBatchAsync(id, commentCursor, 128, ct);
            if (batch.Documents.Count == 0) break;
            await index.BulkImportCommentsAsync(batch.Documents, ct); commentCursor = batch.LastId;
        }
        await index.DeleteCaptionsByMediaGuidAsync(work.MediaGuid, ct);
        long captionCursor = 0;
        while (true)
        {
            var batch = await query.GetCaptionsForMediaBatchAsync(id, captionCursor, 1, ct);
            if (batch.Documents.Count == 0) break;
            await index.BulkImportCaptionsAsync(await captions.HydrateAsync(batch.Documents, ct, requireComplete: true), ct); captionCursor = batch.LastId;
        }
    }
}
