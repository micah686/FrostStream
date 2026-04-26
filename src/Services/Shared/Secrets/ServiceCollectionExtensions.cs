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
}
