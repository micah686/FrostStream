using FlySwattr.NATS.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Shared.Secrets;
using Worker.Services;

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
            // set true to hide "Application started/stopped" messages
            o.SuppressStatusMessages = false;
        });

        var natsUrl = builder.Configuration.GetConnectionString("nats")
            ?? builder.Configuration["NATS:Url"]
            ?? "nats://localhost:4222";
        var natsAuth = BuildNatsAuth(builder.Configuration);

        builder.Services.AddEnterpriseNATSMessaging(options =>
        {
            options.Core.Url = natsUrl;
            options.Core.NatsAuth = natsAuth;
            options.EnableTopologyProvisioning = false;
            options.EnablePayloadOffloading = false;
            options.EnableResilience = false;
            options.EnableCaching = false;
            options.EnableDistributedLock = false;
            options.EnableDlqAdvisoryListener = false;
        });

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        // AddFrostStreamStorage() requires NATS to be wired into this service first;
        // hook it in here once Worker actually consumes IBlobStorageProvider.

        // Register startup service (download initial binaries,...)
        builder.Services.AddHostedService<StartupService>();

        // Stub command consumers for the download flow. Replace each handler's Task.Delay
        // with the real yt-dlp / IBlobStorageProvider implementation when ready.
        builder.Services.AddHostedService<DownloadCommandsConsumerService>();

        var app = builder.Build();

        await app.RunAsync(); // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }

    private static NatsAuthOpts? BuildNatsAuth(IConfiguration configuration)
    {
        var token = configuration["NATS:Token"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            return new NatsAuthOpts { Token = token };
        }

        var username = configuration["NATS:Username"];
        var password = configuration["NATS:Password"];
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return new NatsAuthOpts
            {
                Username = username,
                Password = password
            };
        }

        var credsFile = configuration["NATS:CredsFile"];
        if (!string.IsNullOrWhiteSpace(credsFile))
        {
            return new NatsAuthOpts { CredsFile = credsFile };
        }

        return null;
    }
}
