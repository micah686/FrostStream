using DataBridge.Data;
using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Messaging;

internal sealed class ProviderHaltRetryService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ProviderHaltRetryService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueRetriesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provider halt retry scan failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task DispatchDueRetriesAsync(CancellationToken ct)
    {
        IReadOnlyList<ProviderHaltRetryCandidate> due;
        using (var scope = scopeFactory.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            due = await jobs.GetDueProviderHaltRetriesAsync(clock.GetCurrentInstant(), ct);
        }

        foreach (var candidate in due)
        {
            if (candidate.SourceKind is DownloadSourceKind.Direct)
                continue;

            logger.LogInformation(
                "Dispatching automatic restart for halted JobId {JobId} RetryAt {RetryAt} SourceKind {SourceKind}.",
                candidate.JobId,
                candidate.RetryAt,
                candidate.SourceKind);

            var response = await messageBus.RequestAsync<RestartHaltedDownloadRequest, RestartHaltedDownloadResponse>(
                DownloadSubjects.RestartHaltedDownloadRequest,
                new RestartHaltedDownloadRequest { JobId = candidate.JobId, RequestedBy = "system:auto-retry" },
                TimeSpan.FromSeconds(30),
                ct);

            if (response is null || !response.Success)
            {
                logger.LogWarning(
                    "Automatic restart request for JobId {JobId} was rejected: {ErrorCode} {ErrorMessage}",
                    candidate.JobId,
                    response?.ErrorCode,
                    response?.ErrorMessage);
            }
        }
    }
}
