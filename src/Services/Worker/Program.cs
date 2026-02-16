using FlySwattr.NATS.Extensions;
using FlySwattr.NATS.Hosting.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shared;
using Shared.Messages;
using Shared.Topology;
using Worker.Handlers;

namespace Worker;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddEnterpriseNATSMessaging(opts =>
        {
            opts.Core.Url = builder.Configuration["NATS:Url"] ?? "nats://localhost:4222";
        });

        // Force ConsoleLifetime so Ctrl+C / SIGTERM triggers StopAsync on hosted services
        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o =>
        {
            // set true to hide "Application started/stopped" messages
            o.SuppressStatusMessages = false;
        });

        // Register topology and map the file-processors consumer to its handler
        builder.Services.AddNatsTopologyWithConsumers<JobsTopology>(topology =>
        {
            topology.MapConsumer<FileDownloadRequest>(
                Consumers.FileProcessors,
                FileProcessHandler.HandleAsync);
        });

        var app = builder.Build();
        await app.RunAsync(); // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }
}
