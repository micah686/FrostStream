using DataBridge;
using Lite;
using DataBridge.Persistence;
using WebAPI;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureLiteHost();
// Schema-only initialization does not start the runtime.
if (builder.Configuration.GetValue<bool>("Persistence:InitializeOnly"))
{
    builder.AddDataBridgePersistence();
    await using var schemaHost = builder.Build();
    schemaHost.InitializeDataBridge();
    schemaHost.Logger.LogInformation("Application database initialized ({Provider}). Runtime was not started.",
        schemaHost.Services.GetRequiredService<PersistenceOptions>().Provider);
    return;
}

builder.AddServiceDefaults();
builder.AddLiteModules();
var app = builder.Build();
app.MapWebAPIModule();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
}).AllowAnonymous();
await app.RunAsync();
