using Shared.Secrets;
using Shared.Deployment;
using FrostStream.Integrations.Typesense;
using Microsoft.Extensions.Hosting;
using FrostStream.Full.Infrastructure;

using DataBridge;

namespace FrostStream.Full.DataBridge;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddFullMessaging();
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.AddFullPersistence();
        builder.AddDataBridgeModule();
        builder.AddTypesenseIntegration();
        var app = builder.Build();
        await app.InitializeApplicationAsync();
        await app.RunAsync();
    }
}
