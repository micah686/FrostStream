using NodaTime;

namespace DataBridge.Data;

public interface IImportSessionPurger
{
    Task<ImportSessionCleanupResult> PurgeAsync(
        int retentionDays,
        Func<string, Task>? reportProgress,
        CancellationToken cancellationToken);
}

public sealed record ImportSessionCleanupResult
{
    public int RetentionDays { get; init; }
    public int PurgedSessions { get; init; }
    public int DeletedFlows { get; init; }

    public string Describe()
        => $"Deleted {PurgedSessions} import session(s) and {DeletedFlows} local-import flow instance(s).";
}
