using Microsoft.Extensions.DependencyInjection;
namespace Shared.Deployment;
public static class ModuleRegistration
{
    private sealed record ModuleMarker(Type Type);
    public static bool TryAddModule(this IServiceCollection services, Type module)
    {
        if (services.Any(d => d.ImplementationInstance is ModuleMarker marker && marker.Type == module))
            return false;
        services.AddSingleton(new ModuleMarker(module));
        return true;
    }

}
