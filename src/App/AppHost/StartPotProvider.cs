namespace AppHost;

// bgutil-ytdlp-pot-provider server. Mints YouTube Proof-of-Origin tokens for yt-dlp. In dev we run a
// single provider container; a co-located DataBridge POT broker answers Worker pot.request messages
// from it over NATS. In production each broker host runs its own nearby provider container.
//
// The image tag MUST match YtDlpBinaryDownloaderOptions.BgUtilPluginVersion (the project requires the
// server and the yt-dlp plugin to be the same version).
public static class StartPotProvider
{
    public static IResourceBuilder<ContainerResource> Start(
        IDistributedApplicationBuilder builder,
        AppHostHardeningOptions hardening)
    {
        return builder
            //MUST match YtDlpBinaryDownloaderOptions.BgUtilPluginVersion — bgutil requires the
            // provider server and the yt-dlp plugin to be the same version.
            .AddContainer("pot-provider", "brainicism/bgutil-ytdlp-pot-provider", "1.3.1")
            .WithHttpEndpoint(port: 4416, targetPort: 4416, name: "http")
            .WithHttpHealthCheck("/ping");
    }
}
