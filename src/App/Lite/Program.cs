using DataBridge;
using Lite;
using DataBridge.Persistence;
using WebAPI;

var builder = WebApplication.CreateBuilder(args);
// Schema initialization is intentionally available without starting unported consumers/flows.
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
app.InitializeDataBridge();
app.MapWebAPIModule();
await app.RunAsync();
