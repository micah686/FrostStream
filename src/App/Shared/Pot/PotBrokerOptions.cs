namespace Shared.Pot;

/// <summary>
/// Configuration for a POT broker: a NATS consumer that answers <c>pot.request</c> messages by
/// replaying them against a nearby bgutil-ytdlp-pot-provider container.
/// </summary>
public sealed record PotBrokerOptions
{
    public const string SectionName = "PotBroker";

    /// <summary>Whether this host runs a POT broker. Off by default; enable only where a provider is nearby.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base URL of the nearby bgutil provider (e.g. <c>http://localhost:4416</c>).</summary>
    public string? ProviderUrl { get; init; }

    /// <summary>How often the broker polls the provider's <c>/ping</c> to decide whether to serve requests.</summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Upper bound on a single provider call (token generation can take several seconds on cold start).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
