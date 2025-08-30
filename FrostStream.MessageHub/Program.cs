using System;
using System.Threading.Tasks;
using FrostStream.MessageHub.New;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostStream.MessageHub;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "MessageBroker";


        var host = Host.CreateDefaultBuilder(args)
            // Load appsettings.json + appsettings.{Environment}.json + env vars + command-line args
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                // Defaults are already added by CreateDefaultBuilder; keep here if you want to customize
                // cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                // cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                // cfg.AddEnvironmentVariables();
                // cfg.AddCommandLine(args);
            })
            .ConfigureLogging((ctx, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();

                // Set from config: "Logging:LogLevel:Default"
                // e.g., "Information" in production, "Debug" when troubleshooting
                logging.AddConfiguration(ctx.Configuration.GetSection("Logging"));
            })
            .ConfigureServices((ctx, services) =>
            {
                // ---------- Core singletons ----------
                services.AddSingleton<ServiceRegistry>();

                // Configure JobScheduler (plain singleton; no sockets, no threads)
                var dbPath = ctx.Configuration.GetValue<string>("JobScheduler:DatabasePath")
                             ?? "Filename=jobs.db;Mode=Shared"; // LiteDB connection string default
                services.AddSingleton<JobScheduler>(sp =>
                {
                    var registry = sp.GetRequiredService<ServiceRegistry>();
                    var logger = sp.GetRequiredService<ILogger<JobScheduler>>();
                    return new JobScheduler(registry, dbPath, logger);
                });

                // ---------- Hosted services ----------
                // Broker owns the NetMQ sockets/poller and calls into JobScheduler on its NetMQ thread
                services.AddSingleton<BrokerHostedService>();
                services.AddHostedService(sp => sp.GetRequiredService<BrokerHostedService>());

                // Dedicated background cleanup of stale services in the registry
                // Uses defaults: staleCutoff = 5 min, scanPeriod = 30 sec
                // To customize, bind options from config or pass explicit TimeSpan values here.
                services.AddHostedService<ServiceRegistryCleanup>();
            })
            // If you plan to run as a Windows Service or systemd service, uncomment one of these:
            // .UseWindowsService()
            // .UseSystemd()
            .UseConsoleLifetime() // graceful Ctrl+C handling
            .Build();

        await host.RunAsync();
    }
}
