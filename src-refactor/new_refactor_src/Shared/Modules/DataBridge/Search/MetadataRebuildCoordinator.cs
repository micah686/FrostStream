using DataBridge;
using DataBridge.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Search;

public sealed class MetadataRebuildCoordinator(
    IServiceScopeFactory scopeFactory,
    SearchWriterGate writerGate,
    DataBridge.Persistence.Indexing.PendingIndexStore pendingIndex,
    ISearchIndexWriter indexService,
    SearchStatus status,
    CaptionDocumentHydrator captionDocumentHydrator,
    IHostApplicationLifetime applicationLifetime,
    INotificationDispatcher notificationDispatcher,
    ILogger<MetadataRebuildCoordinator> logger) : IMetadataRebuildCoordinator
{
    private const int BatchSize = 500;
    private int _isRunning;

    public MetadataRebuildStartResult StartRebuild(string reason)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            return new MetadataRebuildStartResult(true, "A metadata index rebuild is already running.");

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunRebuildAsync(reason, applicationLifetime.ApplicationStopping);
                }
                catch (Exception ex) { logger.LogError(ex, "Background search rebuild failed."); }
                finally
                {
                    Interlocked.Exchange(ref _isRunning, 0);
                }
            },
            CancellationToken.None);

        return new MetadataRebuildStartResult(true);
    }

    public async Task<MetadataRebuildStartResult> RebuildAsync(string reason, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
            return new MetadataRebuildStartResult(true, "A metadata index rebuild is already running.");

        try
        {
            await RunRebuildAsync(reason, ct);
            return new MetadataRebuildStartResult(true);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private async Task RunRebuildAsync(string reason, CancellationToken ct)
    {
        await writerGate.Semaphore.WaitAsync(ct);
        try
        {
            await pendingIndex.InvalidateAsync(ct);
            logger.LogInformation("Starting search metadata rebuild. Reason: {Reason}", reason);
            status.Set(SearchState.Building);
            await using var generation = await indexService.CreateGenerationAsync(ct);

            await scopeFactory.WithScopedAsync<IMediaDocumentQuery>(async query =>
            {
                await RebuildMediaAsync(query, generation.Writer, ct);
                await RebuildCommentsAsync(query, generation.Writer, ct);
                await RebuildCaptionsAsync(query, generation.Writer, ct);
            });

            // Replay durable intent into the replacement without acknowledging it. Any mutation
            // racing this pass remains pending and is applied to the activated generation.
            await scopeFactory.WithScopedAsync<IMediaDocumentQuery>(async query =>
            {
                using var worker = new PendingIndexWorker(pendingIndex, query, generation.Writer, captionDocumentHydrator,
                    writerGate, Microsoft.Extensions.Logging.Abstractions.NullLogger<PendingIndexWorker>.Instance);
                string cursor = "";
                while (true)
                {
                    var batch = await pendingIndex.GetPendingBatchAsync(cursor, BatchSize, ct);
                    if (batch.Count == 0) break;
                    foreach (var work in batch) await worker.ApplyAsync(work, ct);
                    cursor = batch[^1].MediaGuid;
                }
            });
            await generation.ActivateAsync(ct);
            status.Set(SearchState.Ready);
            logger.LogInformation("Finished search metadata rebuild.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status.ReportFailure("Rebuild canceled; previous generation retained.");
            throw;
        }
        catch (Exception ex)
        {
            status.ReportFailure(ex.Message);
            logger.LogError(ex, "Search metadata rebuild failed; previous generation retained.");
            await notificationDispatcher.NotifyAdminEventAsync(
                NotificationEventKeys.IndexRebuildFailed,
                "FrostStream search index rebuild failed",
                $"Search metadata rebuild failed: {ex.Message}",
                cancellationToken: ct);
            throw;
        }
        finally { writerGate.Semaphore.Release(); }
    }

    private async Task RebuildMediaAsync(IMediaDocumentQuery query, ISearchIndexWriter writer, CancellationToken ct)
    {
        var lastId = 0L;
        var total = 0;

        while (true)
        {
            var batch = await query.GetMediaBatchAsync(lastId, BatchSize, ct);
            if (batch.Documents.Count == 0)
                break;

            await writer.BulkImportMediaAsync(batch.Documents, ct);
            total += batch.Documents.Count;
            lastId = batch.LastId;
            LogProgress("media", total);
        }
    }

    private async Task RebuildCommentsAsync(IMediaDocumentQuery query, ISearchIndexWriter writer, CancellationToken ct)
    {
        var lastId = 0L;
        var total = 0;

        while (true)
        {
            var batch = await query.GetCommentBatchAsync(lastId, BatchSize, ct);
            if (batch.Documents.Count == 0)
                break;

            await writer.BulkImportCommentsAsync(batch.Documents, ct);
            total += batch.Documents.Count;
            lastId = batch.LastId;
            LogProgress("comments", total);
        }
    }

    private async Task RebuildCaptionsAsync(IMediaDocumentQuery query, ISearchIndexWriter writer, CancellationToken ct)
    {
        var lastId = 0L;
        var total = 0;

        while (true)
        {
            var batch = await query.GetCaptionBatchAsync(lastId, BatchSize, ct);
            if (batch.Documents.Count == 0)
                break;

            var captions = await captionDocumentHydrator.HydrateAsync(batch.Documents, ct, requireComplete: true);
            await writer.BulkImportCaptionsAsync(captions, ct);
            total += batch.Documents.Count;
            lastId = batch.LastId;
            LogProgress("captions", total);
        }
    }

    private void LogProgress(string collection, int total)
    {
        if (total % 5_000 == 0)
            logger.LogInformation("search index rebuild imported {Count} {Collection} documents.", total, collection);
    }
}
