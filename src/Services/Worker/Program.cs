using FlySwattr.NATS.Extensions;
using FlySwattr.NATS.Hosting.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shared;
using Shared.Download;
using Shared.Messages;
using Shared.Storage;
using Shared.Topology;
using Worker.Handlers;
using Worker.Services;

namespace Worker;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddEnterpriseNATSMessaging(opts =>
        {
            opts.Core.Url = builder.Configuration.GetConnectionString("nats")
                            ?? builder.Configuration["NATS:Url"]
                            ?? "nats://localhost:4222";
        });

        // Force ConsoleLifetime so Ctrl+C / SIGTERM triggers StopAsync on hosted services
        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o =>
        {
            // set true to hide "Application started/stopped" messages
            o.SuppressStatusMessages = false;
        });

        // Register download service (interface allows swapping implementations)
        // Both interfaces are implemented by YtDlpDownloadService and share the same instance
        builder.Services.AddSingleton<YtDlpDownloadService>();
        builder.Services.AddSingleton<IDownloadService>(sp => sp.GetRequiredService<YtDlpDownloadService>());
        builder.Services.AddSingleton<IIdempotencyKeyGenerator>(sp => sp.GetRequiredService<YtDlpDownloadService>());
        
        // Register worker services
        builder.Services.AddSingleton<FileProcessHandler>();

        // Register job coordination client (decouples Worker from DataBridge subjects)
        builder.Services.AddSingleton<IJobCoordinationClient, NatsJobCoordinationClient>();

        // Register temp cleanup service
        builder.Services.AddHostedService<WorkerCleanupService>();

        // Register topology and map the file-processors consumer to its handler.
        // The handler lambda captures the service collection — at runtime the
        // TopologyConsumerHostedService has its own IServiceProvider, so we use
        // a deferred resolution via the hosting infrastructure.  Because
        // AddNatsTopologyWithConsumers stores the delegate for later execution
        // (after Build), we capture a service-provider accessor via another
        // singleton that the lambda can close over.
        ServiceProviderAccessor? accessor = null;
        builder.Services.AddSingleton(sp =>
        {
            accessor = new ServiceProviderAccessor(sp);
            return accessor;
        });

        builder.Services.AddNatsTopologyWithConsumers<JobsTopology>(topology =>
        {
            topology.MapConsumer<FileDownloadRequest>(
                Consumers.FileProcessors,
                async context =>
                {
                    // accessor will have been populated by the time this runs
                    var handler = accessor!.ServiceProvider.GetRequiredService<FileProcessHandler>();
                    await handler.HandleAsync(context);
                });
        });

        var app = builder.Build();

        // Eagerly resolve the accessor so the lambda has a valid reference
        app.Services.GetRequiredService<ServiceProviderAccessor>();

        await app.RunAsync(); // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }

    /// <summary>
    /// Simple holder to provide deferred IServiceProvider access in MapConsumer lambdas.
    /// </summary>
    private sealed class ServiceProviderAccessor(IServiceProvider serviceProvider)
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
    }
}
