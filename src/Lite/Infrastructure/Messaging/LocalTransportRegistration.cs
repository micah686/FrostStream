using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FrostStream.ApplicationContracts;
namespace Shared.Messaging.Adapters;
public static class LocalTransportRegistration
{
    public static IServiceCollection AddLocalApplicationTransport(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var capacity = configuration.GetValue<int?>("Messaging:Local:SubscriptionCapacity") ?? 256;
        if (capacity <= 0) throw new InvalidOperationException("Messaging:Local:SubscriptionCapacity must be positive.");
        services.TryAddSingleton<IRequestDispatcher>(sp => sp.GetRequiredService<IMessageBus>());
        services.TryAddSingleton<IEventBus>(sp => sp.GetRequiredService<IMessageBus>());
        services.TryAddSingleton<LocalApplicationTransport>(sp => new(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalApplicationTransport>>(), capacity));
        services.Replace(ServiceDescriptor.Singleton<IMessageBus>(sp => sp.GetRequiredService<LocalApplicationTransport>()));
        return services;
    }

}
