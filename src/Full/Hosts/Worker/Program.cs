using Microsoft.Extensions.DependencyInjection;
using Shared.Secrets;
using Microsoft.Extensions.Hosting;
using FrostStream.Full.Infrastructure;

using Worker;

namespace FrostStream.Full.Worker;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddFullMessaging();
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.AddWorkerModule();
        var app = builder.Build();
        await app.RunAsync();
    }
}
