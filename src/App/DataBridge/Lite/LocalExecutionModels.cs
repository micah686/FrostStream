using System.Text.Json;
using Npgsql;
using Shared.Application;

namespace DataBridge.Lite;

public enum LocalExecutionStatus
{
    Queued,
    Running,
    RetryWaiting,
    CancellationRequested,
    Completed,
    Cancelled,
    Failed
}

public sealed record LocalExecutionItem(
    Guid WorkId,
    string Kind,
    string DeduplicationKey,
    JsonElement Payload,
    LocalExecutionStatus Status,
    int Attempt,
    int MaximumAttempts,
    DateTimeOffset AvailableAt,
    DateTimeOffset UpdatedAt,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int ProgressSequence = 0,
    double? ProgressPercent = null,
    string? ProgressMessage = null);

public sealed record LocalExecutionEvent(
    Guid WorkId,
    string Kind,
    LocalExecutionStatus Status,
    int Attempt,
    DateTimeOffset OccurredAt,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int ProgressSequence = 0,
    double? ProgressPercent = null,
    string? ProgressMessage = null);

public sealed record LocalExecutionSnapshot(
    string StreamKey,
    DateTimeOffset LoadedAt,
    IReadOnlyList<LocalExecutionItem> Items);

public sealed class LiteExecutionOptions
{
    public const string SectionName = "LiteExecution";
    public int MaximumConcurrency { get; set; } = 4;
    public int DownloadConcurrency { get; set; } = 1;
    public int WakeCapacity { get; set; } = 32;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan OwnershipProbeInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// A controlled local handler boundary. Stage 10 deliberately ships no media handlers; Stage 11
/// connects the real download/discovery/import implementations through this interface.
/// </summary>
public interface ILocalExecutionHandler
{
    string Kind { get; }
    string ConcurrencyGroup => Kind;
    Task<WorkExecutionResult> ExecuteAsync(LocalExecutionItem item, CancellationToken cancellationToken);
}

public interface ILocalExecutionStore
{
    Task<LocalExecutionItem> EnqueueAsync(
        string kind,
        string deduplicationKey,
        JsonElement payload,
        int maximumAttempts = 3,
        CancellationToken cancellationToken = default);
    Task RecoverInterruptedAsync(CancellationToken cancellationToken = default);
    Task<LocalExecutionItem?> ClaimNextAsync(CancellationToken cancellationToken = default);
    Task<LocalExecutionStatus?> CompleteAsync(LocalExecutionItem item, WorkExecutionResult result, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(Guid workId, CancellationToken cancellationToken = default);
    Task<bool> ReportProgressAsync(
        Guid workId,
        int sequence,
        double? percent,
        string message,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task<LocalExecutionSnapshot> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default);
}

public interface ILiteExecutionLease
{
    CancellationToken OwnershipLost { get; }
    bool IsOwned { get; }
    int? BackendProcessId { get; }
    Task<T> ExecuteOwnedAsync<T>(
        Func<NpgsqlConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
