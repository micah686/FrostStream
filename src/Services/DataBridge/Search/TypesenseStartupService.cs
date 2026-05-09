using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Typesense;

namespace DataBridge.Search;

public sealed class TypesenseStartupService(
    ITypesenseClient client,
    ITypesenseIndexService indexService,
    IMetadataRebuildCoordinator rebuildCoordinator,
    ILogger<TypesenseStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WaitForTypesenseAsync(cancellationToken);
        var createdAny = await indexService.EnsureAllCollectionsAsync(cancellationToken);
        var mediaDocumentCount = await indexService.GetDocumentCountAsync(
            MediaCollectionSchema.CollectionName,
            cancellationToken);

        if (createdAny || mediaDocumentCount == 0)
        {
            var reason = createdAny
                ? "missing collection created at startup"
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
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
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
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Typesense health check failed on attempt {Attempt}; retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), ct);
            }
        }
    }
}
