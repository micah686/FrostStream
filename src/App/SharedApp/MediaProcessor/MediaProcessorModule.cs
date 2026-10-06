using Shared.Messaging.Adapters;
using Conduit.NATS;
using MediaProcessor.Audio;
using MediaProcessor.Ffmpeg;
using MediaProcessor.Storage;
using MediaProcessor.Thumbnails;
using MediaProcessor.Video;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Shared.Messaging;

using Shared.Deployment;

namespace MediaProcessor;

public static class MediaProcessorModule
{
    public static IHostApplicationBuilder AddMediaProcessorModule(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(MediaProcessorModule))) return builder;
        builder.AddDeployment();
        builder.Services.AddApplicationTransport(builder.Configuration);

        var natsUrl = builder.Configuration.GetConnectionString("nats")
            ?? builder.Configuration["NATS:Url"]
            ?? "nats://localhost:24040";
        var natsAuth = BuildNatsAuth(builder.Configuration);

        builder.Services.AddModuleNats(options =>
        {
            options.Url = natsUrl;
            options.AuthOpts = natsAuth;
            options.EnableTopologyProvisioning = true;
        });

        builder.Services.AddModuleTopology<BackgroundJobsTopology>();
        builder.Services.AddOptions<MediaProcessorOptions>()
            .Bind(builder.Configuration.GetSection(MediaProcessorOptions.SectionName));
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode == DeploymentMode.Lite)
            builder.Services.AddSingleton<IMediaProcessorStorageClient, LocalMediaProcessorStorageClient>();
        else
        {
            builder.Services.AddHttpClient<MediaProcessorStorageClient>();
            builder.Services.AddTransient<IMediaProcessorStorageClient>(sp => sp.GetRequiredService<MediaProcessorStorageClient>());
        }
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddKeyedSingleton<IBackgroundRunReporter>("media-processor", (sp, _) => new BackgroundRunReporter(
            sp.GetRequiredService<FrostStream.ApplicationContracts.IMessageBus>(),
            sp.GetRequiredService<IClock>(),
            "media-processor",
            sp.GetService<Microsoft.Extensions.Logging.ILogger<BackgroundRunReporter>>()));
        builder.Services.AddSingleton<FfmpegRunner>();
        builder.Services.AddHostedService<AudioRenditionProcessorService>();
        builder.Services.AddHostedService<StreamRenditionProcessorService>();
        builder.Services.AddHostedService<MediaThumbnailGenerationService>();

        // Force ConsoleLifetime so Ctrl+C / SIGTERM triggers StopAsync on hosted services
        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o =>
        {
            // set true to hide “Application started/stopped” messages
            o.SuppressStatusMessages = false;
        });

        return builder;
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
