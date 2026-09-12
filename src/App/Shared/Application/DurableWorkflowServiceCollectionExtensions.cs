using Microsoft.Extensions.DependencyInjection;

namespace Shared.Application;

public static class DurableWorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddDurableWorkflowAbstractions(this IServiceCollection services)
    {
        services.AddSingleton(typeof(ILocalProgressHub<,>), typeof(BoundedLocalProgressHub<,>));
        return services;
    }
}
