namespace Shared.Backups;

public sealed record CreateBackupJobRequest(
    string? Name,
    string? Type,
    bool Scheduled = false,
    string? ScheduleKey = null,
    string? IdempotencyKey = null);

public sealed record VerifyBackupRequest(string? Label = null, bool Deep = false);

public sealed record BackupJobDto(
    Guid JobId,
    string Kind,
    string? Type,
    string Status,
    string? Name,
    string? Label,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> Progress)
{
    public string? ResultSummary { get; init; }
    public string? ActualType { get; init; }
    public BackupInfoDto? Backup { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>One backup in the pgBackRest repository (from `pgbackrest info`).</summary>
public sealed record BackupInfoDto(
    string Label,
    string Type,
    string? Name,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DatabaseSize,
    long? RepositorySize,
    string? WalStart,
    string? WalStop,
    bool HasError,
    bool OpenBaoExportPresent,
    bool? KeyRingBackupPresent = null)
{
    public IReadOnlyList<string>? Dependencies { get; init; }
}

public sealed record PitrWindowDto(DateTimeOffset? Earliest, DateTimeOffset? LatestApprox);

public sealed record BackupRepositoryDto(
    bool RepositoryOk,
    string? StatusMessage,
    IReadOnlyList<BackupInfoDto> Backups,
    PitrWindowDto PitrWindow,
    string? DatabasePath = null,
    string? BackupDirectory = null,
    string? KeyRingPath = null);

public sealed record BackupHealthDto(DateTimeOffset ObservedAt, bool Healthy, int WalReadySegments, long RepositoryFreeBytes, long DataFreeBytes, string? ArchiverStatistics, IReadOnlyList<string> Problems);

public interface IBackupServiceClient
{
    Task<BackupHealthDto> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Backup health is not supported in this deployment.");
    Task<BackupJobDto> MaintenanceAsync(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException("Backup maintenance is not supported in this deployment.");
    Task<BackupJobDto> CreateAsync(CreateBackupJobRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<BackupRepositoryDto> ListBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupJobDto> VerifyAsync(VerifyBackupRequest request, CancellationToken cancellationToken = default);
}
