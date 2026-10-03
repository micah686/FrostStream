namespace DataBridge.Persistence.Queries;

internal sealed record ProviderQuery(string Postgres, string Sqlite);

internal static class ApplicationQueryCatalog
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new IReadOnlyDictionary<string, ProviderQuery>[]
    {
        DownloadsQueries.Statements,
        LiveChatQueries.Statements,
        MaintenanceQueries.Statements,
        MediaFilesQueries.Statements,
        MetadataReadQueries.Statements,
        MetadataWriteQueries.Statements,
        PoliciesQueries.Statements,
        RenditionsQueries.Statements,
        StatisticsQueries.Statements,
        WatchStatesQueries.Statements,
    }.SelectMany(statements => statements).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
