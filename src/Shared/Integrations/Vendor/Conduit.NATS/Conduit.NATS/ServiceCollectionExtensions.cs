using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Client.ObjectStore;

namespace Conduit.NATS;

/// <summary>
/// The library's only DI entry points: <see cref="AddNats(IHostApplicationBuilder, string, Action{ConduitNatsOptions}?)"/>
/// wires the connection, bus, JetStream, stores, and (optionally) topology provisioning;
/// <see cref="AddNatsTopologySource{T}"/> registers a declarative topology source.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Conduit.NATS using the Aspire logical connection name to resolve the server URL.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="connectionName">Connection-string name (e.g. <c>"nats"</c>) to read the URL from.</param>
    /// <param name="configure">Optional further configuration of <see cref="ConduitNatsOptions"/>.</param>
    public static IHostApplicationBuilder AddNats(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<ConduitNatsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var connectionString = builder.Configuration.GetConnectionString(connectionName);
        var applicationName = builder.Environment.ApplicationName;

        builder.Services.AddNats(opts =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
                opts.Url = connectionString;
            opts.ClientName ??= applicationName;
            configure?.Invoke(opts);
        });

        return builder;
    }

    /// <summary>
    /// Adds Conduit.NATS to a service collection (use when no <see cref="IHostApplicationBuilder"/> is available).
    /// </summary>
    public static IServiceCollection AddNats(this IServiceCollection services, Action<ConduitNatsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<ConduitNatsOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ConduitNatsOptions>, ConduitNatsOptionsValidator>());

        // Connection + contexts
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ConduitNatsOptions>>().Value;
            var jsonOptions = o.JsonSerializerOptions
                ?? JsonSerializerRegistry.CreateDefaultOptions(o.JsonTypeInfoResolver);
            var registry = new JsonSerializerRegistry(jsonOptions);

            var natsOpts = NatsOpts.Default with
            {
                Url = o.Url,
                Name = o.ClientName ?? NatsOpts.Default.Name,
                AuthOpts = o.AuthOpts ?? NatsAuthOpts.Default,
                TlsOpts = o.TlsOpts ?? NatsOpts.Default.TlsOpts,
                ReconnectWaitMin = o.ReconnectWaitMin ?? NatsOpts.Default.ReconnectWaitMin,
                MaxReconnectRetry = o.MaxReconnect ?? NatsOpts.Default.MaxReconnectRetry,
                DrainSubscriptionsOnDispose = o.DrainOnDispose,
                RetryOnInitialConnect = true,
                SerializerRegistry = registry
            };
            return new NatsConnection(natsOpts);
        });
        services.TryAddSingleton<INatsConnection>(sp => sp.GetRequiredService<NatsConnection>());
        services.TryAddSingleton<INatsJSContext>(sp => new NatsJSContext(sp.GetRequiredService<NatsConnection>()));
        services.TryAddSingleton<INatsKVContext>(sp => new NatsKVContext(sp.GetRequiredService<INatsJSContext>()));
        services.TryAddSingleton<INatsObjContext>(sp => new NatsObjContext(sp.GetRequiredService<INatsJSContext>()));

        // Core message bus
        services.TryAddSingleton<IMessageBus>(sp => new NatsMessageBus(
            sp.GetRequiredService<INatsConnection>(),
            sp.GetRequiredService<ILogger<NatsMessageBus>>(),
            sp.GetRequiredService<IOptions<ConduitNatsOptions>>().Value.RequestTimeout));

        // JetStream publish/consume (single instance behind both interfaces)
        services.TryAddSingleton<NatsJetStreamBus>(sp => new NatsJetStreamBus(
            sp.GetRequiredService<INatsJSContext>(),
            sp.GetRequiredService<ILogger<NatsJetStreamBus>>()));
        services.TryAddSingleton<IJetStreamPublisher>(sp => sp.GetRequiredService<NatsJetStreamBus>());
        services.TryAddSingleton<IJetStreamConsumer>(sp => sp.GetRequiredService<NatsJetStreamBus>());

        // Object store passthrough (per-bucket factory + caching registry)
        services.TryAddSingleton(sp =>
        {
            var objContext = sp.GetRequiredService<INatsObjContext>();
            var logger = sp.GetRequiredService<ILogger<NatsObjectStore>>();
            return new ObjectStoreRegistry(bucket => new NatsObjectStore(objContext, bucket, logger));
        });
        services.TryAddSingleton<Func<string, IObjectStore>>(sp => sp.GetRequiredService<ObjectStoreRegistry>().GetOrCreate);

        // Topology
        services.TryAddSingleton<ITopologyManager, NatsTopologyManager>();
        services.TryAddSingleton<ITopologyReadySignal, TopologyReadySignal>();

        services.AddSingleton<IHostedService>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ConduitNatsOptions>>().Value;
            if (o.EnableTopologyProvisioning)
            {
                return new TopologyProvisioner(
                    sp.GetServices<ITopologySource>(),
                    sp.GetRequiredService<ITopologyManager>(),
                    sp.GetRequiredService<NatsConnection>(),
                    sp.GetRequiredService<ITopologyReadySignal>(),
                    sp.GetRequiredService<ILogger<TopologyProvisioner>>(),
                    sp.GetRequiredService<IOptions<ConduitNatsOptions>>());
            }

            // Without provisioning, optionally validate reachability; otherwise mark ready immediately.
            sp.GetRequiredService<ITopologyReadySignal>().SignalReady();
            return new NatsStartupValidation(
                sp.GetRequiredService<INatsConnection>(),
                sp.GetRequiredService<ILogger<NatsStartupValidation>>(),
                sp.GetRequiredService<IOptions<ConduitNatsOptions>>());
        });

        return services;
    }

    /// <summary>Registers a declarative <see cref="ITopologySource"/> for startup provisioning.</summary>
    public static IServiceCollection AddNatsTopologySource<T>(this IServiceCollection services)
        where T : class, ITopologySource
    {
        services.AddSingleton<ITopologySource, T>();
        return services;
    }
}
