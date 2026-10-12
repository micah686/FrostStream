using Shared.Secrets;
using FrostStream.Full.Security;
using Microsoft.Extensions.Hosting;
using FrostStream.Full.Infrastructure;

using WebAPI;

namespace FrostStream.Full.WebAPI;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddFullMessaging(provisionTopology: false);
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.AddFullSecurity();
        builder.AddWebAPIModule();
        builder.AddFullBackupClient();
        builder.Services.AddHealthChecks().AddCheck<FullReadinessHealthCheck>("application-handlers");
        var app = builder.Build();
        app.MapWebAPIModule();
        app.MapDefaultEndpoints();
        await app.RunAsync();
    }
}
