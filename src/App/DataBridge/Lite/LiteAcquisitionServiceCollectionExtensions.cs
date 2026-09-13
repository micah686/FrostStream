using DataBridge.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YtDlpSharpLib;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Rendering;

namespace DataBridge.Lite;

public static class LiteAcquisitionServiceCollectionExtensions
{
    public static IServiceCollection AddLiteAcquisition(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LiteAcquisitionOptions>()
            .Bind(configuration.GetSection(LiteAcquisitionOptions.SectionName))
            .Validate(x => !string.IsNullOrWhiteSpace(x.YtDlpPath), "YtDlpPath is required.")
            .Validate(x => !string.IsNullOrWhiteSpace(x.FfmpegPath), "FfmpegPath is required.")
            .Validate(x => !string.IsNullOrWhiteSpace(x.TempPath), "TempPath is required.")
            .Validate(x => x.StorageProbeTimeout > TimeSpan.Zero, "StorageProbeTimeout must be positive.")
            .ValidateOnStart();
        services.AddOptions<LitePotOptions>()
            .Bind(configuration.GetSection(LitePotOptions.SectionName))
            .Validate(x => !x.Enabled || Uri.TryCreate(x.ProviderUrl, UriKind.Absolute, out var uri)
                                      && uri.Scheme is "http" or "https",
                "ProviderUrl must be an absolute HTTP(S) URL when LitePot is enabled.")
            .Validate(x => !x.Enabled || Uri.TryCreate(x.ProxyBaseUrl, UriKind.Absolute, out _),
                "ProxyBaseUrl must be absolute when LitePot is enabled.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IYtDlpArgumentRenderer, YtDlpArgumentRenderer>();
        services.TryAddSingleton<IYtDlpProcessFactory, YtDlpProcessFactory>();
        services.TryAddSingleton<IYtDlpClient>(provider =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LiteAcquisitionOptions>>().Value;
            return new YtDlpClient(new YtDlpClientOptions
            {
                YtDlpExecutablePath = configured.YtDlpPath,
                FfmpegExecutablePath = configured.FfmpegPath,
                MinimumDelayBetweenProcessStarts = configured.MinimumDelayBetweenYtDlpStarts
            }, provider.GetRequiredService<IYtDlpProcessFactory>(),
                provider.GetRequiredService<IYtDlpArgumentRenderer>(), provider.GetRequiredService<TimeProvider>());
        });

        services.AddHttpClient<ILitePotProvider, LitePotProvider>((provider, client) =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LitePotOptions>>().Value;
            client.Timeout = configured.RequestTimeout > TimeSpan.Zero ? configured.RequestTimeout : TimeSpan.FromSeconds(30);
        });
        // Lite emits durable local-execution progress directly over its in-process SSE hub.
        // The shared repository's Full-mode notifier is NATS-backed, so keep it explicitly
        // disabled here instead of pulling the distributed transport into Lite.
        services.TryAddSingleton<IDownloadJobStateNotifier>(NullDownloadJobStateNotifier.Instance);
        services.TryAddScoped<IDownloadJobsRepository, DownloadJobsRepository>();
        services.TryAddScoped<IDownloadFlowV2Repository, DownloadFlowV2Repository>();
        services.TryAddScoped<IMetadataRepository, MetadataRepository>();
        services.TryAddScoped<IOptionPresetsRepository, OptionPresetsRepository>();
        services.TryAddScoped<IPlaylistsRepository, PlaylistsRepository>();
        services.TryAddScoped<ICreatorDiscoveryRepository, CreatorDiscoveryRepository>();
        services.TryAddScoped<IImportSessionRepository, ImportSessionRepository>();
        services.AddSingleton<ILiteDownloadIngress, LiteDownloadIngress>();
        services.AddSingleton<ILiteDiscoveryAndImportIngress, LiteDiscoveryAndImportIngress>();
        services.AddSingleton<LiteDownloadExecutionHandler>();
        services.AddSingleton<ILocalExecutionHandler>(provider => provider.GetRequiredService<LiteDownloadExecutionHandler>());
        services.AddSingleton<Shared.Application.IDownloadWorkExecutor>(provider => provider.GetRequiredService<LiteDownloadExecutionHandler>());
        services.AddSingleton<LitePlaylistExecutionHandler>();
        services.AddSingleton<ILocalExecutionHandler>(provider => provider.GetRequiredService<LitePlaylistExecutionHandler>());
        services.AddSingleton<Shared.Application.IPlaylistExpansionExecutor>(provider => provider.GetRequiredService<LitePlaylistExecutionHandler>());
        services.AddSingleton<LiteCreatorScanExecutionHandler>();
        services.AddSingleton<ILocalExecutionHandler>(provider => provider.GetRequiredService<LiteCreatorScanExecutionHandler>());
        services.AddSingleton<Shared.Application.ICreatorScanExecutor>(provider => provider.GetRequiredService<LiteCreatorScanExecutionHandler>());
        services.AddSingleton<LiteImportExecutionHandler>();
        services.AddSingleton<ILocalExecutionHandler>(provider => provider.GetRequiredService<LiteImportExecutionHandler>());
        services.AddSingleton<Shared.Application.IImportWorkExecutor>(provider => provider.GetRequiredService<LiteImportExecutionHandler>());
        services.AddHostedService<LiteAcquisitionRecoveryService>();
        return services;
    }
}
