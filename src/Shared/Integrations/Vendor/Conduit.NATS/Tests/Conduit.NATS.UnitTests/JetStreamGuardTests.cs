using Conduit.NATS;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.JetStream;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

/// <summary>
/// Guard-clause coverage for <c>NatsJetStreamBus</c> that runs without a broker: every guard fires
/// before the substituted JetStream context is touched, so we also assert the context stays untouched.
/// </summary>
public sealed class JetStreamGuardTests
{
    private record TestMsg(string Id);

    private static (NatsJetStreamBus Bus, INatsJSContext Ctx) CreateBus()
    {
        var ctx = Substitute.For<INatsJSContext>();
        var bus = new NatsJetStreamBus(ctx, NullLogger<NatsJetStreamBus>.Instance);
        return (bus, ctx);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task PublishAsync_Requires_MessageId(string? messageId)
    {
        var (bus, ctx) = CreateBus();

        await Should.ThrowAsync<ArgumentException>(
            async () => await bus.PublishAsync("s.subject", new TestMsg("1"), messageId));

        ctx.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task PublishBatchAsync_Rejects_Item_Without_MessageId()
    {
        var (bus, ctx) = CreateBus();
        var batch = new[]
        {
            new BatchMessage<TestMsg>("s.a", new TestMsg("1"), "id-1"),
            new BatchMessage<TestMsg>("s.b", new TestMsg("2"), "")
        };

        await Should.ThrowAsync<ArgumentException>(async () => await bus.PublishBatchAsync(batch));
        ctx.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task PublishBatchAsync_Empty_Is_NoOp()
    {
        var (bus, ctx) = CreateBus();
        await bus.PublishBatchAsync(Array.Empty<BatchMessage<TestMsg>>());
        ctx.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task ConsumeAsync_Rejects_NonPositive_BatchSize(int batchSize)
    {
        var (bus, _) = CreateBus();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await bus.ConsumeAsync<TestMsg>(
            StreamName.From("S"),
            SubjectName.From("s.x"),
            _ => Task.CompletedTask,
            new JetStreamConsumeOptions { BatchSize = batchSize }));
    }

    [Test]
    public async Task ConsumeAsync_Rejects_NonPositive_MaxConcurrency()
    {
        var (bus, _) = CreateBus();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await bus.ConsumeAsync<TestMsg>(
            StreamName.From("S"),
            SubjectName.From("s.x"),
            _ => Task.CompletedTask,
            new JetStreamConsumeOptions { MaxConcurrency = 0 }));
    }
}
