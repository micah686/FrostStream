using Microsoft.Extensions.DependencyInjection;
using Shared.Deployment;
namespace DataBridge.Persistence;
public static class SchemaInitializationRegistration
{
    public static void AddSchemaInitialization(this IServiceCollection services)
        => services.AddSingleton(new ApplicationInitializationStep("application schema", 10, (sp, ct) =>
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<IApplicationSchemaInitializer>().Initialize(ct);
            return Task.CompletedTask;
        }));
}
