using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataBridge.Search;

public sealed class TypesenseStartupService(
    TypesenseInitialization initialization,
    ITypesenseIndexService indexService,
    IMetadataRebuildCoordinator rebuildCoordinator,
    ILogger<TypesenseStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await initialization.ValidateAsync(cancellationToken);
        var mediaDocumentCount = await indexService.GetDocumentCountAsync(
            MediaCollectionSchema.CollectionName,
            cancellationToken);

        // Collections and their field compatibility were already checked read-only above. An
        // empty derived index is runtime recovery work, so rebuild it from PostgreSQL.
        if (mediaDocumentCount == 0)
        {
            var result = await rebuildCoordinator.RebuildAsync("media collection is empty at startup", cancellationToken);
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

}
