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
        type = type?.Trim().ToLowerInvariant() ?? "full";
        if (!(type == "full" && capabilities.Backups.Full || type == "diff" && capabilities.Backups.Differential))
            throw new NotSupportedException(capabilities.Backups.Provider == "SQLite"
                ? "Lite supports full SQLite snapshots only; differential and point-in-time backups are not supported."
                : $"Backup type '{type}' is not supported in this deployment.");
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
            repository.Backups
                .Select(x => new BackupSummaryResponse(
                    x.Label, x.Type, x.Name, x.StartedAt, x.CompletedAt, x.DatabaseSize, x.RepositorySize,
                    x.WalStart, x.WalStop, x.HasError, x.OpenBaoExportPresent))
                .ToArray(),
            new PitrWindowResponse(repository.PitrWindow.Earliest, repository.PitrWindow.LatestApprox),
            repository.DatabasePath, repository.BackupDirectory);
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
            job.ErrorMessage, job.CreatedAt, job.CompletedAt, job.Progress);
}
