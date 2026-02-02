using FlySwattr.NATS.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Extensions;

public static class NatsConfigurationExtensions
{
    public static IServiceCollection AddVideoPlatformNats(
        this IServiceCollection services, 
        IConfiguration config)
    {
        return services.AddEnterpriseNATSMessaging(opts =>
        {
            // Shared defaults across all services
            opts.Core.Url = config.GetConnectionString("nats") ?? "nats://localhost:4222";
            opts.Core.MaxConcurrency = 100;
            
            // All services use the same topology
            opts.EnableTopologyProvisioning = false; // Only ControlPlane provisions!
            opts.EnableDlqAdvisoryListener = true;
            opts.PayloadOffloading.ThresholdBytes = 64 * 1024; // 64KB
        });
    }
}