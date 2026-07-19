using DataBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataBridge.Search;

public interface IMetadataRebuildCoordinator
{
    MetadataRebuildStartResult StartRebuild(string reason);
    Task<MetadataRebuildStartResult> RebuildAsync(string reason, CancellationToken ct = default);
}

public sealed record MetadataRebuildStartResult(bool Accepted, string? ErrorMessage = null);

public sealed class MetadataRebuildCoordinator(
    IServiceScopeFactory scopeFactory,
    ITypesenseIndexService indexService,
    CaptionDocumentHydrator captionDocumentHydrator,
    IHostApplicationLifetime applicationLifetime,
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
        try
        {
            logger.LogInformation("Starting Typesense metadata rebuild. Reason: {Reason}", reason);
            await indexService.RecreateAllCollectionsAsync(ct);

            await scopeFactory.WithScopedAsync<IMediaDocumentQuery>(async query =>
            {
                await RebuildMediaAsync(query, ct);
                await RebuildCommentsAsync(query, ct);
                await RebuildCaptionsAsync(query, ct);
            });

            logger.LogInformation("Finished Typesense metadata rebuild.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Typesense metadata rebuild was canceled during shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Typesense metadata rebuild failed.");
        }
    }

    private async Task RebuildMediaAsync(IMediaDocumentQuery query, CancellationToken ct)
    {
        var lastId = 0L;
        var total = 0;

        while (true)
        {
            var batch = await query.GetMediaBatchAsync(lastId, BatchSize, ct);
            if (batch.Documents.Count == 0)
                break;

            await indexService.BulkImportMediaAsync(batch.Documents, ct);
            total += batch.Documents.Count;
            lastId = batch.LastId;
            LogProgress("media", total);
        }
    }

    private async Task RebuildCommentsAsync(IMediaDocumentQuery query, CancellationToken ct)
    {
        var lastId = 0L;
        var total = 0;

        while (true)
        {
            var batch = await query.GetCommentBatchAsync(lastId, BatchSize, ct);
            if (batch.Documents.Count == 0)
                break;

            await indexService.BulkImportCommentsAsync(batch.Documents, ct);
            total += batch.Documents.Count;
            lastId = batch.LastId;
            LogProgress("comments", total);
        }
    }

    private async Task RebuildCaptionsAsync(IMediaDocumentQuery query, CancellationToken ct)
    {
        var lastId = 0L;
        var total = 0;

        while (true)
        {
            var batch = await query.GetCaptionBatchAsync(lastId, BatchSize, ct);
            if (batch.Documents.Count == 0)
                break;

            var captions = await captionDocumentHydrator.HydrateAsync(batch.Documents, ct);
            await indexService.BulkImportCaptionsAsync(captions, ct);
            total += batch.Documents.Count;
            lastId = batch.LastId;
            LogProgress("captions", total);
        }
    }

    private void LogProgress(string collection, int total)
    {
        if (total % 5_000 == 0)
            logger.LogInformation("Typesense rebuild imported {Count} {Collection} documents.", total, collection);
    }
}
