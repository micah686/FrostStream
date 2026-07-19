using Conduit.NATS;
using MediaProcessor.Audio;
using MediaProcessor.Ffmpeg;
using MediaProcessor.Storage;
using MediaProcessor.Video;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Shared.Messaging;

namespace MediaProcessor;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        var natsUrl = builder.Configuration.GetConnectionString("nats")
            ?? builder.Configuration["NATS:Url"]
            ?? "nats://localhost:24040";
        var natsAuth = BuildNatsAuth(builder.Configuration);

        builder.Services.AddNats(options =>
        {
            options.Url = natsUrl;
            options.AuthOpts = natsAuth;
            options.EnableTopologyProvisioning = true;
        });

        builder.Services.AddNatsTopologySource<BackgroundJobsTopology>();
        builder.Services.AddOptions<MediaProcessorOptions>()
            .Bind(builder.Configuration.GetSection(MediaProcessorOptions.SectionName));
        builder.Services.AddHttpClient<MediaProcessorStorageClient>();
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<FfmpegRunner>();
        builder.Services.AddHostedService<AudioRenditionProcessorService>();
        builder.Services.AddHostedService<StreamRenditionProcessorService>();

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
