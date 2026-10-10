using Microsoft.Extensions.Options;
using YtDlpSharpLib.Options;

namespace Worker.Services;

/// <summary>
/// Appends the bgutil POT <c>--extractor-args</c> + <c>--plugin-dirs</c> to a set of yt-dlp options so
/// YouTube requests can fetch a Proof-of-Origin token via the loopback shim → NATS broker → provider
/// path. Used by both the download path and the metadata/listing path. The extractor key targets only
/// YouTube, so it's inert for other sources.
/// </summary>
public sealed class PotOptionsApplier(
    IOptions<PotProviderOptions> potOptions,
    PotShimEndpoint potShimEndpoint)
{
    /// <summary>
    /// Returns <paramref name="options"/> with the bgutil POT args appended (never replacing caller
    /// values). When POT is disabled or the shim isn't up, returns <paramref name="options"/> unchanged
    /// (including <see langword="null"/>), so callers can pass the result straight through as override options.
    /// </summary>
    public YtDlpOptions? Apply(YtDlpOptions? options)
    {
        var baseUrl = potShimEndpoint.BaseUrl;
        if (!potOptions.Value.Enabled || string.IsNullOrEmpty(baseUrl))
        {
            return options;
        }

        var resolved = options ?? new YtDlpOptions();

        var extractorArgs = new List<string>(resolved.Extractor.ExtractorArgs)
        {
            $"youtubepot-bgutilhttp:base_url={baseUrl}"
        };

        // Keep "default" so the built-in plugin directories still resolve alongside ours.
        var pluginDirs = new List<string>(resolved.General.PluginDirs);
        var potPluginDir = GetPotPluginDir();
        if (!pluginDirs.Contains(potPluginDir))
        {
            pluginDirs.Add(potPluginDir);
        }
        if (!pluginDirs.Contains("default"))
        {
            pluginDirs.Add("default");
        }

        return resolved with
        {
            Extractor = resolved.Extractor with { ExtractorArgs = extractorArgs },
            General = resolved.General with { PluginDirs = pluginDirs }
        };
    }

    private static string GetPotPluginDir()
        => Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp-plugins");
}
