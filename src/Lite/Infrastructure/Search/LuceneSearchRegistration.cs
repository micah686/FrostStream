using DataBridge.Persistence;
using DataBridge.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Shared.Deployment;

namespace FrostStream.Lite.Infrastructure.Search;

public static class LuceneSearchRegistration
{
    public static IHostApplicationBuilder AddLuceneSearch(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(LuceneSearchRegistration))) return builder;
        var persistence = PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        if (persistence.Provider != PersistenceProvider.Sqlite || DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Lite)
            throw new InvalidOperationException("Embedded search requires Lite and SQLite.");
        var path = Path.GetFullPath(builder.Configuration["Search:Lucene:Path"]
            ?? Path.Combine(Path.GetDirectoryName(persistence.SqlitePath)!, "search"), builder.Environment.ContentRootPath);
        var ramBuffer = builder.Configuration.GetValue<double?>("Search:Lucene:RamBufferMiB") ?? 16;
        var maximumDepth = builder.Configuration.GetValue<int?>("Search:Lucene:MaximumPageDepth") ?? 100_000;
        if (ramBuffer is < 4 or > 64 || !double.IsFinite(ramBuffer) || maximumDepth is < 250 or > 100_000)
            throw new InvalidOperationException("Lucene requires a 4–64 MiB RAM buffer and a 250–100000 maximum page depth.");
        if (path == persistence.SqlitePath || path == Path.GetDirectoryName(persistence.SqlitePath))
            throw new InvalidOperationException("Search requires its own rebuildable directory, separate from the application database.");
        builder.Services.AddSingleton(new LuceneSearchOptions { DirectoryPath = path, RamBufferMiB = ramBuffer, MaximumPageDepth = maximumDepth });
        builder.Services.AddSingleton<LuceneSearchService>();
        builder.Services.AddSingleton<ISearchReader>(sp => sp.GetRequiredService<LuceneSearchService>());
        builder.Services.AddSingleton<ISearchIndexWriter>(sp => sp.GetRequiredService<LuceneSearchService>());
        builder.Services.AddSingleton<ISearchActivationBarrier, SqliteSearchActivationBarrier>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("embedded search", 50, async (sp, ct) =>
        {
            var missing = await sp.GetRequiredService<LuceneSearchService>().EnsureAllCollectionsAsync(ct);
            var database = sp.GetRequiredService<ApplicationDatabase>();
            await using var invalidation = database.CreateCommand("SELECT invalidated FROM search_index_state WHERE id=1");
            var restored = Convert.ToInt64(await invalidation.ExecuteScalarAsync(ct)) != 0;
            if (missing || restored)
                await sp.GetRequiredService<IMetadataRebuildCoordinator>().RebuildAsync(missing ? "missing, corrupt, or incompatible embedded index" : "restored application database", ct);
            else sp.GetRequiredService<SearchStatus>().Set(SearchState.Ready);
            await using var clear = database.CreateCommand("UPDATE search_index_state SET invalidated=0 WHERE id=1");
            await clear.ExecuteNonQueryAsync(ct);
        }));
        return builder;
    }
}
