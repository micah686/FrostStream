namespace Shared.Backups;

public sealed record CreateBackupJobRequest(string? Name, string? Mode, bool Scheduled = false, string? IdempotencyKey = null);

public sealed record BackupJobDto(
    Guid JobId,
    string Status,
    string? ArchivePath,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record BackupArchiveDto(
    string ArchivePath,
    DateTimeOffset? CreatedAt,
    bool MediaIncluded,
    int SchemaVersion,
    string Mode);

public sealed record VerifyBackupDto(bool Success, string? ErrorMessage);

public sealed record RestorePlanDto(bool PreflightOk, string RestoreCommand, string? ErrorMessage);

public interface IBackupServiceClient
{
    Task<BackupJobDto> CreateAsync(string? name, string? mode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupArchiveDto>> ListArchivesAsync(CancellationToken cancellationToken = default);
    Task<VerifyBackupDto> VerifyAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<RestorePlanDto> BuildRestorePlanAsync(string archivePath, CancellationToken cancellationToken = default);
}
