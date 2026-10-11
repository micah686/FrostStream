using Microsoft.Extensions.Hosting;

using Scheduler;

namespace FrostStream.Full.Scheduler;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddSchedulerModule();
        var app = builder.Build();
        app.MapSchedulerModule();
        app.MapDefaultEndpoints();
        await app.RunAsync();
    }
}
