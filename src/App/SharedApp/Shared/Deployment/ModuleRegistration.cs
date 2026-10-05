using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Deployment;

public static class ModuleRegistration
{
    private sealed record ModuleMarker(Type Type);
    private sealed class TransportMarker;
    private sealed class LocalTransportMarker;

    public static void UseLocalModuleTransport(this IServiceCollection services)
        {
        if (!services.Any(d => d.ServiceType == typeof(LocalTransportMarker)))
            services.AddSingleton<LocalTransportMarker>();
    }

    public static bool TryAddModule(this IServiceCollection services, Type module)
    {
        if (services.Any(d => d.ImplementationInstance is ModuleMarker marker && marker.Type == module))
            return false;
        services.AddSingleton(new ModuleMarker(module));
        return true;
    }

    /// <summary>Combined hosts configure the shared transport once, before registering modules.</summary>
    public static IServiceCollection AddModuleNats(this IServiceCollection services, Action<ConduitNatsOptions> configure)
    {
        if (services.Any(d => d.ServiceType == typeof(LocalTransportMarker) || d.ServiceType == typeof(TransportMarker)))
            return services;
        services.AddSingleton<TransportMarker>();
        return services.AddNats(configure);
    }

    public static IServiceCollection AddModuleTopology<T>(this IServiceCollection services) where T : class, ITopologySource
    {
        if (services.Any(d => d.ServiceType == typeof(LocalTransportMarker))) return services;
        if (!services.Any(d => d.ServiceType == typeof(ITopologySource) && d.ImplementationType == typeof(T)))
            services.AddNatsTopologySource<T>();
        return services;
    }
}
