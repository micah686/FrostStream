using Shared.Backups;
using Shared.Deployment;
using WebAPI.Features.Backups.Models;

namespace WebAPI.Features.Backups;

public sealed class BackupJobService(IBackupServiceClient client, SystemCapabilities capabilities)
{
    public async Task<BackupJobResponse> StartBackupAsync(
        string? name,
        string? type,
        CancellationToken cancellationToken)
    {
        type = BackupTypes.Normalize(type);
        var supported = type switch
        {
            BackupTypes.Full => capabilities.Backups.Full,
            BackupTypes.Differential => capabilities.Backups.Differential,
            BackupTypes.Incremental => capabilities.Backups.Incremental,
            _ => false
        };
        if (!supported)
            throw new NotSupportedException(capabilities.Backups.Provider == "SQLite"
                ? "Lite supports full SQLite snapshots only."
                : $"Backup type '{type}' is not supported in this deployment.");
        if (capabilities.Backups.Provider == "PostgreSQL")
            name = BackupTypes.ValidateName(name);
        return ToResponse(await client.CreateAsync(new CreateBackupJobRequest(name, type), cancellationToken));
    }

    public async Task<IReadOnlyList<BackupJobResponse>> ListJobsAsync(CancellationToken cancellationToken)
        => (await client.ListJobsAsync(cancellationToken)).Select(ToResponse).ToArray();

    public async Task<BackupJobResponse?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
        => await client.GetJobAsync(jobId, cancellationToken) is { } job ? ToResponse(job) : null;

    public async Task<BackupRepositoryResponse> ListBackupsAsync(CancellationToken cancellationToken)
    {
        var repository = await client.ListBackupsAsync(cancellationToken);
        return new BackupRepositoryResponse(
            repository.RepositoryOk,
            repository.StatusMessage,
            repository.Backups.Select(ToResponse).ToArray(),
            new PitrWindowResponse(repository.PitrWindow.Earliest, repository.PitrWindow.LatestApprox),
            repository.DatabasePath, repository.BackupDirectory, repository.KeyRingPath);
    }

    public Task<BackupHealthDto> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (capabilities.Backups.Provider != "PostgreSQL") throw new NotSupportedException("Backup health is only available for PostgreSQL.");
        return client.GetHealthAsync(cancellationToken);
    }

    public async Task<BackupJobResponse> MaintenanceAsync(string operation, CancellationToken cancellationToken)
    {
        if (capabilities.Backups.Provider != "PostgreSQL") throw new NotSupportedException("Backup maintenance is only available for PostgreSQL.");
        return ToResponse(await client.MaintenanceAsync(operation, cancellationToken));
    }

    public async Task<BackupJobResponse> VerifyAsync(
        string? label,
        bool deep,
        CancellationToken cancellationToken)
    {
        if (!capabilities.Backups.Verification || deep && !capabilities.Backups.DeepVerification)
            throw new NotSupportedException(capabilities.Backups.Provider == "SQLite"
                ? "Lite supports SQLite integrity verification only; deep restore verification is not supported."
                : "This verification operation is not supported in this deployment.");
        return ToResponse(await client.VerifyAsync(new Shared.Backups.VerifyBackupRequest(label, deep), cancellationToken));
    }

    private static BackupJobResponse ToResponse(BackupJobDto job)
        => new(
            job.JobId, job.Kind, job.Type, job.Status, job.Name, job.Label,
            job.ErrorMessage, job.CreatedAt, job.CompletedAt, job.Progress)
        {
            ResultSummary = job.ResultSummary,
            ActualType = job.ActualType,
            StartedAt = job.StartedAt,
            Backup = job.Backup is { } backup ? ToResponse(backup) : null
        };

    private static BackupSummaryResponse ToResponse(BackupInfoDto backup)
        => new(
            backup.Label, backup.Type, backup.Name, backup.StartedAt, backup.CompletedAt, backup.DatabaseSize,
            backup.RepositorySize, backup.WalStart, backup.WalStop, backup.HasError, backup.OpenBaoExportPresent,
            backup.KeyRingBackupPresent)
        {
            Dependencies = backup.Dependencies
        };
}
