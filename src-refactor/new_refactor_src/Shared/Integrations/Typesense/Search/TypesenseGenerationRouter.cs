using Typesense;

namespace DataBridge.Search;

/// <summary>One persistent alias selects all three collections as one generation.</summary>
public sealed class TypesenseGenerationRouter(ITypesenseClient client)
{
    public const string Alias = "froststream_search_active";
    private string? _active;
    public string Name(string collection) => collection + "_" + (Volatile.Read(ref _active)
        ?? throw new InvalidOperationException("Search has not been initialized."));
    public void Activate(string generation) => Volatile.Write(ref _active, generation);
    public async Task<bool> LoadAsync(CancellationToken ct)
    {
        try
        {
            var alias = await client.RetrieveCollectionAlias(Alias, ct);
            var mediaName = alias.CollectionName;
            if (!mediaName.StartsWith("media_", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid search generation alias.");
            var generation = mediaName[6..];
            foreach (var name in new[] { "media", "comments", "captions" })
                await client.RetrieveCollection(name + "_" + generation, ct);
            Activate(generation);
            return true;
        }
        catch (TypesenseApiNotFoundException) { return false; }
    }
}
