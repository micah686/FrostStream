using Conduit.NATS.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.IntegrationTests;

public sealed class ProvisioningTopology : ITopologySource
{
    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec { Name = StreamName.From("PROV"), Subjects = ["prov.>"] };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        yield return new ConsumerSpec
        {
            StreamName = StreamName.From("PROV"),
            DurableName = ConsumerName.From("provc"),
            FilterSubject = "prov.x",
            AckPolicy = AckPolicy.Explicit
        };
    }

    public IEnumerable<BucketSpec> GetBuckets()
    {
        yield return new BucketSpec { Name = BucketName.From("provkv") };
    }

    public IEnumerable<ObjectStoreSpec> GetObjectStores()
    {
        yield return new ObjectStoreSpec { Name = BucketName.From("provobj") };
    }
}

[ClassDataSource<NatsFixture>(Shared = SharedType.PerTestSession)]
public sealed class TopologyProvisioningTests
{
    private readonly NatsFixture _nats;

    public TopologyProvisioningTests(NatsFixture nats) => _nats = nats;

    [Test]
    [Timeout(60_000)]
    public async Task TopologyManager_Ensure_Is_Idempotent(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var topo = sp.GetRequiredService<ITopologyManager>();

        var stream = new StreamSpec { Name = StreamName.From("IDEMP"), Subjects = ["idemp.>"] };
        var consumer = new ConsumerSpec
        {
            StreamName = StreamName.From("IDEMP"),
            DurableName = ConsumerName.From("idempc"),
            FilterSubject = "idemp.x"
        };

        // Running twice must not throw and must not duplicate resources.
        await topo.EnsureStreamAsync(stream, cancellationToken);
        await topo.EnsureStreamAsync(stream, cancellationToken);
        await topo.EnsureConsumerAsync(consumer, cancellationToken);
        await topo.EnsureConsumerAsync(consumer, cancellationToken);

        var js = sp.GetRequiredService<INatsJSContext>();
        (await js.GetStreamAsync("IDEMP", cancellationToken: cancellationToken)).Info.Config.Name.ShouldBe("IDEMP");
        (await js.GetConsumerAsync("IDEMP", "idempc", cancellationToken)).Info.Name.ShouldBe("idempc");
    }

    [Test]
    [Timeout(120_000)]
    public async Task Provisioner_HostedService_Signals_Ready_And_Reruns_Cleanly(CancellationToken cancellationToken)
    {
        // First host: provisions everything end-to-end and signals ready.
        await RunHostAndAssertReadyAsync(cancellationToken);

        // Second host against the same server: every Ensure* is now a no-op, still signals ready.
        await RunHostAndAssertReadyAsync(cancellationToken);
    }

    private async Task RunHostAndAssertReadyAsync(CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddNats(o =>
        {
            o.Url = _nats.ConnectionString;
            o.EnableTopologyProvisioning = true;
            o.ValidateConnectionOnStart = false;
        });
        builder.Services.AddNatsTopologySource<ProvisioningTopology>();

        using var host = builder.Build();
        await host.StartAsync(cancellationToken);
        try
        {
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyCts.CancelAfter(TimeSpan.FromSeconds(60));

            var signal = host.Services.GetRequiredService<ITopologyReadySignal>();
            await signal.WaitAsync(readyCts.Token);
            signal.IsSignaled.ShouldBeTrue();

            var js = host.Services.GetRequiredService<INatsJSContext>();
            (await js.GetStreamAsync("PROV", cancellationToken: cancellationToken)).Info.Config.Name.ShouldBe("PROV");
        }
        finally
        {
            await host.StopAsync(cancellationToken);
        }
    }
}
