using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Provisioning;
using YtDlpSharpLib.Rendering;
using YtDlpSharpLib.Scheduling;

namespace YtDlpSharpLib;

/// <summary>
/// DI registration extensions for YtDlpSharp.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IYtDlpClient"/> and its supporting services in the container.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration callback for <see cref="YtDlpClientOptions"/>.</param>
    public static IServiceCollection AddYtDlpClient(
        this IServiceCollection services,
        Action<YtDlpClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<YtDlpClientOptions>();
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IYtDlpArgumentRenderer, YtDlpArgumentRenderer>();
        services.TryAddSingleton<IYtDlpProcessFactory, YtDlpProcessFactory>();
        services.TryAddSingleton<IYtDlpProcessStartGate, YtDlpProcessStartGate>();
        services.TryAddSingleton<IYtDlpClient, YtDlpClient>();
        services.TryAddSingleton<IYtDlpExecutionScheduler, YtDlpExecutionScheduler>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IYtDlpBinaryDownloader"/> and its supporting services. If the container
    /// already has an <see cref="HttpClient"/> registration (e.g., via
    /// <c>services.AddHttpClient&lt;YtDlpBinaryDownloader&gt;()</c>), it is used. Otherwise the
    /// downloader creates and disposes its own.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration callback for <see cref="YtDlpBinaryDownloaderOptions"/>.</param>
    public static IServiceCollection AddYtDlpBinaryDownloader(
        this IServiceCollection services,
        Action<YtDlpBinaryDownloaderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<YtDlpBinaryDownloaderOptions>();
        }

        services.TryAddSingleton<IYtDlpBinaryDownloader>(static sp =>
        {
            var optionsValue = sp.GetRequiredService<IOptions<YtDlpBinaryDownloaderOptions>>().Value;
            var httpClient = sp.GetService<HttpClient>();
            return new YtDlpBinaryDownloader(optionsValue, httpClient);
        });

        return services;
    }
}
