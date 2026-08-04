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
    IReadOnlyList<string> Progress);

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
    bool OpenBaoExportPresent);

public sealed record PitrWindowDto(DateTimeOffset? Earliest, DateTimeOffset? LatestApprox);

public sealed record BackupRepositoryDto(
    bool RepositoryOk,
    string? StatusMessage,
    IReadOnlyList<BackupInfoDto> Backups,
    PitrWindowDto PitrWindow);

public interface IBackupServiceClient
{
    Task<BackupJobDto> CreateAsync(CreateBackupJobRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<BackupRepositoryDto> ListBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupJobDto> VerifyAsync(VerifyBackupRequest request, CancellationToken cancellationToken = default);
}
