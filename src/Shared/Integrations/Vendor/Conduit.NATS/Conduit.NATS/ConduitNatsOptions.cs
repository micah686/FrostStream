using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Conduit.NATS;

/// <summary>
/// The single, strongly-typed options object for the library, validated on startup.
/// Almost everything the v3 client now handles (reconnect, OTel, drain) needs no configuration here.
/// </summary>
public sealed class ConduitNatsOptions
{
    /// <summary>NATS server URL (e.g. <c>nats://localhost:4222</c>). Required.</summary>
    public string Url { get; set; } = "nats://localhost:4222";

    /// <summary>Logical client name surfaced to the server. Defaults to the application name.</summary>
    public string? ClientName { get; set; }

    /// <summary>Authentication options (token, user/password, or creds file).</summary>
    public NatsAuthOpts? AuthOpts { get; set; }

    /// <summary>TLS options. When null, the client default is used.</summary>
    public NatsTlsOpts? TlsOpts { get; set; }

    /// <summary>Minimum reconnect backoff. When null, the client default is used.</summary>
    public TimeSpan? ReconnectWaitMin { get; set; }

    /// <summary>Maximum reconnect attempts. When null, the client default (retry forever) is used.</summary>
    public int? MaxReconnect { get; set; }

    /// <summary>Default request/reply timeout used by <see cref="IMessageBus.RequestAsync{TRequest,TResponse}"/>.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Plug a source-generated <see cref="JsonTypeInfoResolver"/> (e.g. <c>MyJsonContext.Default</c>) to make the
    /// library's default serializer AOT/source-gen-driven for your contracts. The library still layers on its
    /// NodaTime and string-enum converters, and falls back to reflection for any type the context doesn't cover.
    /// Ignored when <see cref="JsonSerializerOptions"/> is set.
    /// </summary>
    public IJsonTypeInfoResolver? JsonTypeInfoResolver { get; set; }

    /// <summary>
    /// Full override of the JSON serialization options. When set, the library uses these verbatim — supply your
    /// own resolver/converters here for strict AOT (no reflection fallback). Takes precedence over
    /// <see cref="JsonTypeInfoResolver"/>.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>When true, the v3 client drains in-flight messages on dispose.</summary>
    public bool DrainOnDispose { get; set; } = true;

    /// <summary>When true, registered <see cref="ITopologySource"/> specs are provisioned on startup.</summary>
    public bool EnableTopologyProvisioning { get; set; } = true;

    /// <summary>When true, startup validation pings the server (and verifies provisioned streams).</summary>
    public bool ValidateConnectionOnStart { get; set; } = true;

    /// <summary>Per-attempt timeout while waiting for the NATS connection at startup.</summary>
    public TimeSpan StartupConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Overall budget for establishing the connection during topology provisioning.</summary>
    public TimeSpan TotalStartupTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

internal sealed class ConduitNatsOptionsValidator : IValidateOptions<ConduitNatsOptions>
{
    public ValidateOptionsResult Validate(string? name, ConduitNatsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
            return ValidateOptionsResult.Fail("ConduitNatsOptions.Url must be set.");

        if (options.RequestTimeout <= TimeSpan.Zero)
            return ValidateOptionsResult.Fail("ConduitNatsOptions.RequestTimeout must be greater than zero.");

        if (options.MaxReconnect is < -1)
            return ValidateOptionsResult.Fail("ConduitNatsOptions.MaxReconnect must be -1 (infinite) or non-negative.");

        return ValidateOptionsResult.Success;
    }
}
