using DataBridge.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using Typesense;

namespace DataBridge.Search;

public sealed class TypesenseStartupService(
    ITypesenseClient client,
    ITypesenseIndexService indexService,
    IMetadataRebuildCoordinator rebuildCoordinator,
    IClock clock,
    INotificationDispatcher notificationDispatcher,
    ILogger<TypesenseStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WaitForTypesenseAsync(cancellationToken);
        var createdAny = await indexService.EnsureAllCollectionsAsync(cancellationToken);
        var mediaDocumentCount = await indexService.GetDocumentCountAsync(
            MediaCollectionSchema.CollectionName,
            cancellationToken);

        // A pre-existing collection from an earlier schema lacks the current fields; a rebuild
        // recreates it with the current schema and backfills. added_at_sort (ingestion timestamp,
        // used for "Recently added") is the newest addition.
        var hasCurrentFields = await indexService.MediaCollectionHasFieldAsync("added_at_sort", cancellationToken);

        if (createdAny || mediaDocumentCount == 0 || !hasCurrentFields)
        {
            var reason = createdAny ? "missing collection created at startup"
                : !hasCurrentFields ? "media collection schema is missing current fields"
                : "media collection is empty at startup";
            var result = await rebuildCoordinator.RebuildAsync(reason, cancellationToken);
            logger.LogInformation(
                "Typesense collection bootstrap rebuild completed; accepted: {Accepted}. {Message}",
                result.Accepted,
                result.ErrorMessage);
        }
        else
        {
            logger.LogInformation("All Typesense metadata collections are present; skipping startup rebuild.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task WaitForTypesenseAsync(CancellationToken ct)
    {
        var deadline = clock.GetCurrentInstant().Plus(Duration.FromSeconds(60));
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await client.RetrieveHealth(ct);
                logger.LogInformation("Typesense health check succeeded on attempt {Attempt}.", attempt);
                return;
            }
            catch (Exception ex) when (clock.GetCurrentInstant() < deadline && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Typesense health check failed on attempt {Attempt}; retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Typesense health check failed after {Attempt} attempt(s).", attempt);
                await notificationDispatcher.NotifyAdminEventAsync(
                    NotificationEventKeys.SystemIntegrationFailed,
                    "FrostStream system integration failed: Typesense",
                    $"Typesense health check failed after {attempt} attempt(s): {ex.Message}",
                    cancellationToken: ct);
                throw;
            }
        }
    }
}
