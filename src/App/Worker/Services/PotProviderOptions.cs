namespace Worker.Services;

/// <summary>
/// Worker-side POT configuration. When enabled, the Worker starts a loopback HTTP→NATS shim and
/// injects the bgutil extractor-args + plugin-dirs into every download so yt-dlp can fetch a
/// Proof-of-Origin token. The actual provider lives behind the <c>pot-brokers</c> NATS queue group,
/// so the Worker needs no provider URL of its own.
/// </summary>
public sealed record PotProviderOptions
{
    public const string SectionName = "PotProvider";

    /// <summary>Master switch (kill-switch). When false, the shim is not started and no POT args are injected.</summary>
    public bool Enabled { get; init; }

    /// <summary>How long the shim waits for a broker to return a token before giving up.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
