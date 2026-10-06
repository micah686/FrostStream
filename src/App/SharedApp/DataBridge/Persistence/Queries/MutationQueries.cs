namespace DataBridge.Persistence.Queries;

internal static class MutationQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["Mutation.LockImportSession"] = new(
            "SELECT * FROM imports.import_sessions WHERE session_id = {fs0} FOR UPDATE",
            "SELECT * FROM imports_import_sessions WHERE session_id = {fs0}"),
    };
}
