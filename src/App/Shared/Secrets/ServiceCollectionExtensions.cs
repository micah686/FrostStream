using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Secrets;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenBaoSecretStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenBaoOptions>(configuration.GetSection(OpenBaoOptions.SectionName));
        services.AddSingleton<ISecretStore, OpenBaoSecretStore>();
        return services;
    }

    public static IServiceCollection AddLocalFileSecretStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var storageRoot = configuration["FROSTSTREAM_STORAGE_ROOT"];
        var section = configuration.GetSection(LocalFileSecretStoreOptions.SectionName);
        var configured = section.Get<LocalFileSecretStoreOptions>() ?? new LocalFileSecretStoreOptions();
        configured.SecretsPath = FirstNonBlank(configured.SecretsPath,
            string.IsNullOrWhiteSpace(storageRoot) ? null : Path.Combine(storageRoot, "secrets"));
        configured.KeyDirectoryPath = FirstNonBlank(configured.KeyDirectoryPath,
            string.IsNullOrWhiteSpace(storageRoot) ? null : Path.Combine(storageRoot, "secret-keys"));

        if (!Path.IsPathFullyQualified(configured.SecretsPath) || !Path.IsPathFullyQualified(configured.KeyDirectoryPath))
            throw new InvalidOperationException(
                "Lite local secrets require absolute LiteSecrets:SecretsPath and LiteSecrets:KeyDirectoryPath values, or an absolute FROSTSTREAM_STORAGE_ROOT.");

        services.Configure<LocalFileSecretStoreOptions>(options =>
        {
            options.SecretsPath = Path.GetFullPath(configured.SecretsPath);
            options.KeyDirectoryPath = Path.GetFullPath(configured.KeyDirectoryPath);
            options.MaximumDocumentBytes = configured.MaximumDocumentBytes;
        });
        services.AddSingleton<LocalFileSecretStore>();
        services.AddSingleton<ISecretStore>(provider => provider.GetRequiredService<LocalFileSecretStore>());
        services.AddSingleton<ILocalSecretBackupManifestProvider, LocalSecretBackupManifestProvider>();
        services.AddHostedService<LocalSecretStoreStartupValidator>();
        return services;
    }

    private static string FirstNonBlank(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;
}
