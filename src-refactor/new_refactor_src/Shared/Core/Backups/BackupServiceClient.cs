using System.Net;
using System.Net.Http.Json;

namespace Shared.Backups;

public sealed class BackupServiceClient(HttpClient httpClient) : IBackupServiceClient
{
    public async Task<BackupJobDto> CreateAsync(
        CreateBackupJobRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/internal/backups/jobs", request, cancellationToken);
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

    public async Task<BackupRepositoryDto> ListBackupsAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<BackupRepositoryDto>("/internal/backups/backups", cancellationToken)
           ?? throw new InvalidOperationException("Backup service returned an empty repository response.");

    public async Task<BackupJobDto> VerifyAsync(
        VerifyBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/internal/backups/verify", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BackupJobDto>(cancellationToken)
               ?? throw new InvalidOperationException("Backup service returned an empty verify response.");
    }
}
