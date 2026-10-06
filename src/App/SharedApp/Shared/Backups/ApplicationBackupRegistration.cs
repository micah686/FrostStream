using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.Deployment;

namespace Shared.Backups;

public static class ApplicationBackupRegistration
{
    public static IServiceCollection AddApplicationBackupClient(this IServiceCollection services, IConfiguration configuration)
    {
        if (DeploymentOptions.FromConfiguration(configuration).Mode == DeploymentMode.Lite)
            services.TryAddSingleton<IBackupServiceClient, UnavailableLocalBackupClient>();
        else if (!services.Any(d => d.ServiceType == typeof(IBackupServiceClient)))
            services.AddHttpClient<IBackupServiceClient, BackupServiceClient>(client =>
                client.BaseAddress = new Uri(configuration["BackupService:BaseUrl"] ?? "http://backupservice"));
        return services;
    }
}

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
