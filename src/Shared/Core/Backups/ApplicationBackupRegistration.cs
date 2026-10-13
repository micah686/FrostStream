using Shared.Backups;
namespace Shared.Backups;
/// <summary>Until Phase 4, Lite preserves the shared routes without contacting PostgreSQL's backup service.</summary>
public sealed class UnavailableLocalBackupClient : IBackupServiceClient
{
    public const string UnavailableMessage = "Local SQLite backup operations are not available yet.";
    private static Task<T> Unavailable<T>(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested ? Task.FromCanceled<T>(cancellationToken)
            : Task.FromException<T>(new LocalBackupUnavailableException(UnavailableMessage));
    public Task<BackupJobDto> CreateAsync(CreateBackupJobRequest request, CancellationToken cancellationToken = default) => Unavailable<BackupJobDto>(cancellationToken);
    public Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default) => Unavailable<IReadOnlyList<BackupJobDto>>(cancellationToken);
    public Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unavailable<BackupJobDto?>(cancellationToken);
    public Task<BackupRepositoryDto> ListBackupsAsync(CancellationToken cancellationToken = default) => Unavailable<BackupRepositoryDto>(cancellationToken);
    public Task<BackupJobDto> VerifyAsync(VerifyBackupRequest request, CancellationToken cancellationToken = default) => Unavailable<BackupJobDto>(cancellationToken);
}

public sealed class LocalBackupUnavailableException(string message) : InvalidOperationException(message);
