using System.Net;
using System.Net.Http.Json;

namespace Shared.Backups;

public sealed class BackupServiceClient(HttpClient httpClient) : IBackupServiceClient
{
    public async Task<BackupJobDto> CreateAsync(string? name, string? mode, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/internal/backups/jobs",
            new CreateBackupJobRequest(name, mode),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BackupJobDto>(cancellationToken)
               ?? throw new InvalidOperationException("Backup service returned an empty job response.");
    }

    public async Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<BackupJobDto[]>("/internal/backups/jobs", cancellationToken) ?? [];

    public async Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/internal/backups/jobs/{jobId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BackupJobDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<BackupArchiveDto>> ListArchivesAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<BackupArchiveDto[]>("/internal/backups/archives", cancellationToken) ?? [];

    public async Task<VerifyBackupDto> VerifyAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/internal/backups/verify",
            new ArchiveRequest(archivePath),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerifyBackupDto>(cancellationToken)
               ?? new VerifyBackupDto(false, "Backup service returned an empty verification response.");
    }

    public async Task<RestorePlanDto> BuildRestorePlanAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/internal/backups/restore-plan",
            new ArchiveRequest(archivePath),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RestorePlanDto>(cancellationToken)
               ?? throw new InvalidOperationException("Backup service returned an empty restore-plan response.");
    }

    private sealed record ArchiveRequest(string ArchivePath);
}
