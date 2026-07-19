using Shared.Backups;
using WebAPI.Features.Backups.Models;

namespace WebAPI.Features.Backups;

public sealed class BackupJobService(IBackupServiceClient client)
{
    public async Task<BackupJobResponse> StartBackupAsync(
        string? name,
        string? mode,
        CancellationToken cancellationToken)
        => ToResponse(await client.CreateAsync(name, mode, cancellationToken));

    public async Task<IReadOnlyList<BackupJobResponse>> ListJobsAsync(CancellationToken cancellationToken)
        => (await client.ListJobsAsync(cancellationToken)).Select(ToResponse).ToArray();

    public async Task<BackupJobResponse?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
        => await client.GetJobAsync(jobId, cancellationToken) is { } job ? ToResponse(job) : null;

    public async Task<IReadOnlyList<BackupSummaryResponse>> ListBackupsAsync(CancellationToken cancellationToken)
        => (await client.ListArchivesAsync(cancellationToken))
            .Select(x => new BackupSummaryResponse(x.ArchivePath, x.CreatedAt, x.MediaIncluded, x.SchemaVersion, x.Mode))
            .ToArray();

    public async Task<VerifyBackupResponse> VerifyAsync(string archivePath, CancellationToken cancellationToken)
    {
        var result = await client.VerifyAsync(archivePath, cancellationToken);
        return new VerifyBackupResponse(result.Success, result.ErrorMessage);
    }

    public async Task<RestorePlanResponse> BuildRestorePlanAsync(string archivePath, CancellationToken cancellationToken)
    {
        var result = await client.BuildRestorePlanAsync(archivePath, cancellationToken);
        return new RestorePlanResponse(result.PreflightOk, result.RestoreCommand, result.ErrorMessage);
    }

    private static BackupJobResponse ToResponse(BackupJobDto job)
        => new(job.JobId, job.Status, job.ArchivePath, job.ErrorMessage, job.CreatedAt, job.CompletedAt);
}
