using Microsoft.Extensions.Hosting;

namespace Scheduler;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddSchedulerModule();
        var app = builder.Build();
        app.MapSchedulerModule();
        await app.RunAsync();
    }
}
