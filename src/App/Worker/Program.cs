using Conduit.NATS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NATS.Client.Core;
using NodaTime;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using Worker.Services;
using YtDlpSharpLib;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Rendering;

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

        builder.Services.AddNats(options =>
        {
            options.Url = natsUrl;
            options.AuthOpts = natsAuth;
            // Provisions every ITopologySource registered below (idempotent — DataBridge
            // registers the same source, so whichever service starts first wins).
            options.EnableTopologyProvisioning = true;
        });

        builder.Services.AddNatsTopologySource<DownloadTopology>();
        builder.Services.AddNatsTopologySource<PlaylistTopology>();
        builder.Services.AddNatsTopologySource<BackgroundJobsTopology>();
        builder.Services.AddNatsTopologySource<LocalImportTopology>();

        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddOpenBaoSecretStore(builder.Configuration);
        builder.Services.AddFrostStreamStorage();
        builder.Services.AddSingleton<IStorageEnumerator, StorageEnumerator>();

        // yt-dlp wiring. The binary downloader writes into <BaseDirectory>/tools and the
        // client points at the predicted absolute paths so the first invocation finds them
        // (StartupService runs first as a plain IHostedService and blocks host startup until
        // the downloads are complete). YtDlpClientOptions and YtDlpBinaryDownloaderOptions are
        // init-only records, so we bypass the AddYtDlpClient/AddYtDlpBinaryDownloader helpers
        // and register the services directly with prebuilt options instances.
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(toolsDirectory);
        var configuredWorkerOptions = builder.Configuration
            .GetSection(WorkerOptions.SectionName)
            .Get<WorkerOptions>() ?? new WorkerOptions();

        var binaryDownloaderOptions = new YtDlpSharpLib.Provisioning.YtDlpBinaryDownloaderOptions
        {
            DefaultDirectory = toolsDirectory,
        };
        builder.Services.AddSingleton<YtDlpSharpLib.Provisioning.IYtDlpBinaryDownloader>(_ =>
            new YtDlpSharpLib.Provisioning.YtDlpBinaryDownloader(binaryDownloaderOptions));

        var ytDlpClientOptions = new YtDlpClientOptions
        {
            YtDlpExecutablePath = Path.Combine(toolsDirectory, YtDlpPaths.YtDlpFileName),
            FfmpegExecutablePath = Path.Combine(toolsDirectory, YtDlpPaths.FfmpegFileName),
            DownloadLimitRate = configuredWorkerOptions.YtDlpLimitRate,
            DownloadThrottledRate = configuredWorkerOptions.YtDlpThrottledRate,
            MinimumDelayBetweenProcessStarts = configuredWorkerOptions.YtDlpMinDelayBetweenStarts
        };
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IYtDlpArgumentRenderer, YtDlpArgumentRenderer>();
        builder.Services.TryAddSingleton<IYtDlpProcessFactory, YtDlpProcessFactory>();
        builder.Services.TryAddSingleton<IYtDlpClient>(sp => new YtDlpClient(
            ytDlpClientOptions,
            sp.GetRequiredService<IYtDlpProcessFactory>(),
            sp.GetRequiredService<IYtDlpArgumentRenderer>(),
            sp.GetRequiredService<TimeProvider>()));

        // POT (Proof-of-Origin Token) wiring. When enabled, the Worker provisions the bgutil plugin,
        // runs a loopback HTTP→NATS shim, and injects the bgutil extractor-args into every download.
        builder.Services.AddOptions<PotProviderOptions>()
            .Bind(builder.Configuration.GetSection(PotProviderOptions.SectionName));
        builder.Services.AddSingleton<PotShimEndpoint>();
        builder.Services.AddSingleton<PotOptionsApplier>();
        builder.Services.AddSingleton<ProviderDownloadHaltRegistry>();
        builder.Services.AddHttpClient<IReturnYouTubeDislikeClient, ReturnYouTubeDislikeClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkerOptions>>()
                .Value
                .ReturnYouTubeDislike;
            client.BaseAddress = options.BaseUrl;
            client.Timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FrostStream-Worker/1.0 (+return-youtube-dislike)");
        });

        // Register startup service (downloads yt-dlp/ffmpeg/ffprobe binaries before any
        // BackgroundService starts).
        builder.Services.AddHostedService<StartupService>();

        // POT shim: starts after StartupService; its constructor publishes the loopback base URL.
        builder.Services.AddHostedService<PotShimService>();

        // Worker tag routing config.
        builder.Services.AddOptions<WorkerOptions>()
            .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName));

        // Channel-asset cache wiring.
        builder.Services.AddOptions<AssetCacheOptions>()
            .Bind(builder.Configuration.GetSection(AssetCacheOptions.SectionName));
        builder.Services.AddSingleton<AssetCacheWriter>();
        builder.Services.AddHttpClient("asset-cache", (sp, client) =>
        {
            var assetOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AssetCacheOptions>>().Value;
            client.Timeout = assetOptions.RequestTimeout > TimeSpan.Zero ? assetOptions.RequestTimeout : TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FrostStream-Worker/1.0 (+asset-cache)");
        });

        // Command consumers for the download flow.
        builder.Services.AddHostedService<DownloadCommandsConsumerService>();
        builder.Services.AddHostedService<LocalImportCommandsConsumerService>();
        builder.Services.AddHostedService<PlaylistCommandsConsumerService>();
        builder.Services.AddHostedService<ChannelDiscoveryConsumerService>();
        builder.Services.AddHostedService<ChannelAssetRefreshConsumerService>();
        builder.Services.AddHostedService<FilesystemRescanConsumerService>();
        builder.Services.AddHostedService<OrphanCleanupConsumerService>();
        builder.Services.AddHostedService<MediaFileDeleteConsumerService>();

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
