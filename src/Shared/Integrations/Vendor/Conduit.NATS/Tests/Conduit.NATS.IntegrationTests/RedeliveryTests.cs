using Conduit.NATS.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.IntegrationTests;

[ClassDataSource<NatsFixture>(Shared = SharedType.PerTestSession)]
public sealed class RedeliveryTests
{
    private readonly NatsFixture _nats;

    public RedeliveryTests(NatsFixture nats) => _nats = nats;

    [Test]
    [Timeout(90_000)]
    public async Task Handler_Failure_Naks_And_Message_Is_Redelivered(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var topo = sp.GetRequiredService<ITopologyManager>();

        await topo.EnsureStreamAsync(new StreamSpec { Name = StreamName.From("FLOW5"), Subjects = ["flow5.>"] });
        await topo.EnsureConsumerAsync(new ConsumerSpec
        {
            StreamName = StreamName.From("FLOW5"),
            DurableName = ConsumerName.From("flow5c"),
            FilterSubject = "flow5.evt",
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(10),
            MaxDeliver = 5
        });

        var attempts = 0;
        var succeeded = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = sp.GetRequiredService<IJetStreamConsumer>();
        await consumer.ConsumeAsync<TestEvent>(
            StreamName.From("FLOW5"),
            SubjectName.From("flow5.evt"),
            async ctx =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1)
                    throw new InvalidOperationException("transient failure → expect Nak + redelivery");

                ctx.Redelivered.ShouldBeTrue();
                await ctx.AckAsync();
                succeeded.TrySetResult(ctx.NumDelivered);
            },
            new JetStreamConsumeOptions { DurableName = ConsumerName.From("flow5c") });

        await sp.GetRequiredService<IJetStreamPublisher>()
            .PublishAsync("flow5.evt", new TestEvent("5", "redeliver-me"), messageId: "flow5-1");

        var deliveredCount = await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        deliveredCount.ShouldBeGreaterThanOrEqualTo(2u);
        attempts.ShouldBeGreaterThanOrEqualTo(2);
    }
}
