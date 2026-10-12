using FrostStream.Lite.Infrastructure;
using Shared.Deployment;
using DataBridge;
using Lite;
using DataBridge.Persistence;
using WebAPI;

namespace Lite;
public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (await DataBridge.Persistence.Sqlite.SqliteRecoveryCommand.RunAsync(builder.Configuration))
        {
            Console.WriteLine("Recovery verification completed. If restored, configure Persistence:Sqlite:Path to <destination>/core.sqlite and Secrets:Local:KeyRingPath to <destination>/core.sqlite.keys before starting.");
            return;
        }
        builder.ConfigureLiteHost();
        // Schema-only initialization does not start the runtime.
        if (builder.Configuration.GetValue<bool>("Persistence:InitializeOnly"))
        {
            builder.AddLitePersistence();
            await using var schemaHost = builder.Build();
            await schemaHost.InitializeApplicationAsync();
            schemaHost.Logger.LogInformation("Application database initialized ({Provider}). Runtime was not started.",
                schemaHost.Services.GetRequiredService<PersistenceOptions>().Provider);
            return;
        }

        builder.AddServiceDefaults();
        builder.AddLiteModules();
        var app = builder.Build();
        await app.InitializeApplicationAsync();
        app.MapWebAPIModule();
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        }).AllowAnonymous();
        await app.RunAsync();
    }
}
