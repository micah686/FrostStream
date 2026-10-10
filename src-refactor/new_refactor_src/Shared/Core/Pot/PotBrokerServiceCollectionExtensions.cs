using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Shared.Pot;

public static class PotBrokerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the POT broker role. The hosted service is always registered but no-ops unless
    /// <c>PotBroker:Enabled</c> is true, so it's safe to call from any host; enable it only on hosts
    /// that sit near a bgutil provider.
    /// </summary>
    public static IServiceCollection AddPotBroker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PotBrokerOptions>()
            .Bind(configuration.GetSection(PotBrokerOptions.SectionName));
        services.AddHostedService<PotBrokerConsumerService>();
        return services;
    }
}
