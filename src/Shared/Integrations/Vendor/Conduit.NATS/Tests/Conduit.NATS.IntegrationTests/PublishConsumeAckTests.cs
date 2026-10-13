using Conduit.NATS.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.IntegrationTests;

[ClassDataSource<NatsFixture>(Shared = SharedType.PerTestSession)]
public sealed class PublishConsumeAckTests
{
    private readonly NatsFixture _nats;

    public PublishConsumeAckTests(NatsFixture nats) => _nats = nats;

    [Test]
    [Timeout(60_000)]
    public async Task Published_Message_Is_Consumed_And_Acked(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var topo = sp.GetRequiredService<ITopologyManager>();

        await topo.EnsureStreamAsync(new StreamSpec
        {
            Name = StreamName.From("FLOW1"),
            Subjects = ["flow1.>"]
        });
        await topo.EnsureConsumerAsync(new ConsumerSpec
        {
            StreamName = StreamName.From("FLOW1"),
            DurableName = ConsumerName.From("flow1c"),
            FilterSubject = "flow1.evt",
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromSeconds(10),
            MaxDeliver = 5
        });

        var received = new TaskCompletionSource<TestEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = sp.GetRequiredService<IJetStreamConsumer>();
        await consumer.ConsumeAsync<TestEvent>(
            StreamName.From("FLOW1"),
            SubjectName.From("flow1.evt"),
            async ctx =>
            {
                await ctx.AckAsync();
                received.TrySetResult(ctx.Message);
            },
            new JetStreamConsumeOptions { DurableName = ConsumerName.From("flow1c") });

        var publisher = sp.GetRequiredService<IJetStreamPublisher>();
        await publisher.PublishAsync("flow1.evt", new TestEvent("1", "hello"), messageId: "flow1-1");

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        got.Id.ShouldBe("1");
        got.Name.ShouldBe("hello");
    }

    [Test]
    [Timeout(60_000)]
    public async Task JetStream_Dedupes_On_MessageId(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var topo = sp.GetRequiredService<ITopologyManager>();
        await topo.EnsureStreamAsync(new StreamSpec { Name = StreamName.From("FLOW1B"), Subjects = ["flow1b.>"] });

        var publisher = sp.GetRequiredService<IJetStreamPublisher>();
        await publisher.PublishAsync("flow1b.evt", new TestEvent("1", "a"), messageId: "dupe-key");
        await publisher.PublishAsync("flow1b.evt", new TestEvent("1", "a"), messageId: "dupe-key");

        var js = sp.GetRequiredService<INatsJSContext>();
        var stream = await js.GetStreamAsync("FLOW1B", cancellationToken: cancellationToken);
        // Both publishes share a message ID within the dedupe window → only one is persisted.
        stream.Info.State.Messages.ShouldBe(1L);
    }
}
