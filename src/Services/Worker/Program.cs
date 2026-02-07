using FluentStorage;
using FlySwattr.NATS.Extensions;
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

        // Register FluentStorage provider modules
        StorageFactory.Modules.UseFtpStorage();
        StorageFactory.Modules.UseSftpStorage();
        StorageFactory.Modules.UseAwsStorage();
        StorageFactory.Modules.UseAzureBlobStorage();
        StorageFactory.Modules.UseGoogleCloudStorage();

        // Register storage handlers
        builder.Services.AddSingleton<Storage.IStorageHandler, Storage.PosixLocalHandler>();
        builder.Services.AddSingleton<Storage.IStorageHandler, Storage.StreamingNetworkHandler>();
        builder.Services.AddSingleton<Storage.IStorageHandler, Storage.ObjectStorageHandler>();
        builder.Services.AddSingleton<Storage.StorageHandlerFactory>();

        builder.Services.AddHostedService<JobProcessingService>();

        var app = builder.Build();
        await app.RunAsync(); // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }
}