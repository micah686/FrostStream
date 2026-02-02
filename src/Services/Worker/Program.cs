using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Worker;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        // Force ConsoleLifetime so Ctrl+C / SIGTERM triggers StopAsync on hosted services
        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o =>
        {
            // set true to hide “Application started/stopped” messages
            o.SuppressStatusMessages = false;
        });

        var app = builder.Build();
        await app.RunAsync();  // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }
}