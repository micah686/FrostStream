using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Secrets;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenBaoSecretStore(this IServiceCollection services, IConfiguration configuration)
    {
        if (Shared.Deployment.DeploymentOptions.FromConfiguration(configuration).Mode != Shared.Deployment.DeploymentMode.Full)
            throw new InvalidOperationException("OpenBao infrastructure requires Full.");
        services.Configure<OpenBaoOptions>(configuration.GetSection(OpenBaoOptions.SectionName));
        services.TryAddSingleton<ISecretStore, OpenBaoSecretStore>();
        return services;
    }
}
