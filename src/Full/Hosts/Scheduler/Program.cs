using Microsoft.Extensions.Hosting;
using FrostStream.Full.Infrastructure;

using Scheduler;

namespace FrostStream.Full.Scheduler;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddFullMessaging();
        builder.AddSchedulerModule();
        builder.AddFullBackupClient();
        var app = builder.Build();
        app.MapSchedulerModule();
        app.MapDefaultEndpoints();
        await app.RunAsync();
    }
}
