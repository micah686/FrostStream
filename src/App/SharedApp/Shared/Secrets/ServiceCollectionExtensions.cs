using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Secrets;

public static class ServiceCollectionExtensions
{
    /// <summary>Lite's database module supplies its local store; Full retains OpenBao.</summary>
    public static IServiceCollection AddApplicationSecretStore(this IServiceCollection services, IConfiguration configuration)
        => Shared.Deployment.DeploymentOptions.FromConfiguration(configuration).Mode == Shared.Deployment.DeploymentMode.Lite
            ? services
            : services.AddOpenBaoSecretStore(configuration);

    public static IServiceCollection AddOpenBaoSecretStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenBaoOptions>(configuration.GetSection(OpenBaoOptions.SectionName));
        services.TryAddSingleton<ISecretStore, OpenBaoSecretStore>();
        return services;
    }
}
