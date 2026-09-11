using Microsoft.Extensions.Logging;
using NodaTime;
using Typesense;

namespace DataBridge.Search;

/// <summary>Finite Typesense setup and read-only runtime compatibility validation.</summary>
public sealed class TypesenseInitialization(
    ITypesenseClient client,
    ITypesenseIndexService indexService,
    IClock clock,
    ILogger<TypesenseInitialization> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await WaitForTypesenseAsync(cancellationToken);
        await indexService.EnsureAllCollectionsAsync(cancellationToken);
        if (!await indexService.MediaCollectionHasFieldAsync("added_at_sort", cancellationToken))
        {
            // Recreate is safe for this derived index; runtime incremental/rebuild work repopulates it.
            await indexService.RecreateAllCollectionsAsync(cancellationToken);
        }
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await WaitForTypesenseAsync(cancellationToken);
        foreach (var collection in new[]
                 {
                     MediaCollectionSchema.CollectionName,
                     CommentsCollectionSchema.CollectionName,
                     CaptionsCollectionSchema.CollectionName
                 })
        {
            try
            {
                await indexService.GetDocumentCountAsync(collection, cancellationToken);
            }
            catch (TypesenseApiNotFoundException ex)
            {
                throw new InvalidOperationException($"Typesense collection '{collection}' is missing. Run the matching init profile.", ex);
            }
        }

        if (!await indexService.MediaCollectionHasFieldAsync("added_at_sort", cancellationToken))
            throw new InvalidOperationException("Typesense media collection schema is incompatible. Run the matching init profile.");
    }

    private async Task WaitForTypesenseAsync(CancellationToken cancellationToken)
    {
        var deadline = clock.GetCurrentInstant().Plus(Duration.FromSeconds(60));
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await client.RetrieveHealth(cancellationToken);
                return;
            }
            catch (Exception ex) when (clock.GetCurrentInstant() < deadline && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Typesense health check failed on attempt {Attempt}; retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), cancellationToken);
            }
        }
    }
}
