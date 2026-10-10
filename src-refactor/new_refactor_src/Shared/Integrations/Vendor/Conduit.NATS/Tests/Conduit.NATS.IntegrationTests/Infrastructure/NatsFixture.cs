using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.Nats;
using TUnit.Core.Interfaces;

namespace Conduit.NATS.IntegrationTests.Infrastructure;

/// <summary>
/// One JetStream-enabled NATS container shared across the whole test session
/// (injected via <c>[ClassDataSource&lt;NatsFixture&gt;(Shared = SharedType.PerTestSession)]</c>).
/// </summary>
public sealed class NatsFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly NatsContainer _container = new NatsBuilder("nats:2.10")
        .WithCommand("--jetstream")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    /// <summary>
    /// Builds a service provider wired with the library's public DI surface, pointed at the container.
    /// Topology provisioning/startup validation are off so tests drive <see cref="ITopologyManager"/> directly.
    /// </summary>
    public ServiceProvider BuildProvider(Action<ConduitNatsOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddNats(o =>
        {
            o.Url = ConnectionString;
            o.EnableTopologyProvisioning = false;
            o.ValidateConnectionOnStart = false;
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public record TestEvent(string Id, string Name);
public record PingMsg(string Value);
public record PongMsg(string Value);
