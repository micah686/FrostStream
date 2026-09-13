using System.Text.Json;
using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Lite;

/// <summary>Repairs the narrow intent-to-ledger crash window without reactivating stopped work.</summary>
public sealed class LiteAcquisitionRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILocalExecutionStore store,
    ILocalExecutionWakeSignal wake,
    ILogger<LiteAcquisitionRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var active = await db.DownloadJobs.AsNoTracking()
            .Where(x => x.Status == DownloadJobStatus.Running && x.CurrentRunId != null)
            .Select(x => new { x.JobId, RunId = x.CurrentRunId!.Value, x.CorrelationId })
            .ToArrayAsync(cancellationToken);
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
        foreach (var row in active)
        {
            var request = await jobs.GetOriginalRequestAsync(row.JobId, cancellationToken);
            if (request is null) continue;
            await store.EnqueueAsync(LiteAcquisitionKinds.Download, $"download:{row.JobId:N}:{row.RunId:N}",
                JsonSerializer.SerializeToElement(new LiteDownloadWork
                {
                    JobId = row.JobId, RunId = row.RunId, CorrelationId = row.CorrelationId,
                    SourceUrl = request.SourceUrl, StorageKey = request.StorageKey ?? "default",
                    PresetKey = request.PresetKey, CookieSecretPath = request.CookieSecretPath,
                    Tags = request.Tags, ForceDownload = request.ForceDownload, SourceKind = request.SourceKind,
                    MediaKind = request.MediaKind, AudioFormat = request.AudioFormat,
                    EncodeAudioRendition = request.EncodeAudioRendition, FetchComments = request.FetchComments,
                    Priority = request.Priority, YtDlpOptions = request.YtDlpOptions
                }, LiteDownloadIngress.CreateJsonOptions()), 3, cancellationToken);
        }
        if (active.Length > 0)
        {
            logger.LogInformation("Reconciled {Count} active Lite acquisition intent(s) with the local ledger.", active.Length);
            wake.Wake();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
