using FrostStream.ApplicationContracts;
using NSubstitute;
using Shared.Messaging.Adapters;
using Shouldly;
using TUnit.Core;
using Nats = Conduit.NATS;

namespace UnitTests.Deployment;

public sealed class NatsApplicationTransportTests
{
    [Test]
    public async Task Request_And_Event_Adapters_Preserve_Subjects_Headers_Timeouts_And_Cancellation()
    {
        var bus = Substitute.For<Nats.IMessageBus>();
        var transport = new NatsApplicationTransport(bus, Substitute.For<Nats.IJetStreamPublisher>(), Substitute.For<Nats.IJetStreamConsumer>());
        using var cts = new CancellationTokenSource();
        var timeout = TimeSpan.FromSeconds(7);
        bus.RequestAsync<string, string>("request.v1", "payload", timeout, cts.Token).Returns("response");
        (await transport.RequestAsync<string, string>("request.v1", "payload", timeout, cts.Token)).ShouldBe("response");
        await transport.PublishAsync("event.v1", "payload", new MessageHeaders(new() { ["trace"] = "abc" }), cts.Token);
        await bus.Received(1).PublishAsync("event.v1", "payload", Arg.Is<Nats.MessageHeaders>(h => h.Headers["trace"] == "abc"), cts.Token);
    }

    [Test]
    public async Task Durable_Adapter_Preserves_Consumer_Options_And_Handler_Acknowledgments()
    {
        var consumer = Substitute.For<Nats.IJetStreamConsumer>();
        var publisher = Substitute.For<Nats.IJetStreamPublisher>();
        var transport = new NatsApplicationTransport(Substitute.For<Nats.IMessageBus>(), publisher, consumer);
        var context = Substitute.For<Nats.IJsMessageContext<string>>();
        context.Message.Returns("job");
        context.Headers.Returns(Nats.MessageHeaders.Empty);
        context.NumDelivered.Returns(3u);
        consumer.ConsumePullAsync(Nats.StreamName.From("jobs"), Nats.ConsumerName.From("worker"),
            Arg.Any<Func<Nats.IJsMessageContext<string>, Task>>(), Arg.Any<Nats.JetStreamConsumeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<Nats.IJsMessageContext<string>, Task>>()(context));
        await transport.ConsumePullAsync<string>(StreamName.From("jobs"), ConsumerName.From("worker"), async message =>
        {
            message.Message.ShouldBe("job");
            message.NumDelivered.ShouldBe(3u);
            await message.InProgressAsync();
            await message.NackAsync(TimeSpan.FromSeconds(2));
            await message.AckAsync();
            await message.TermAsync();
        }, new DurableConsumeOptions { BatchSize = 4, MaxConcurrency = 2, MaxAckPending = 8 });
        await consumer.Received(1).ConsumePullAsync(Nats.StreamName.From("jobs"), Nats.ConsumerName.From("worker"),
            Arg.Any<Func<Nats.IJsMessageContext<string>, Task>>(), Arg.Is<Nats.JetStreamConsumeOptions>(o => o.BatchSize == 4 && o.MaxConcurrency == 2 && o.MaxAckPending == 8), default);
        await context.Received(1).InProgressAsync();
        await context.Received(1).NackAsync(TimeSpan.FromSeconds(2));
        await context.Received(1).AckAsync();
        await context.Received(1).TermAsync();
        await transport.PublishAsync("job.v1", "payload", "stable-id");
        await publisher.Received(1).PublishAsync("job.v1", "payload", "stable-id", null, default);
    }

    [Test]
    public async Task Subscription_Adapter_Invokes_The_Handler_And_Preserves_Reply_And_Lifetime()
    {
        var bus = Substitute.For<Nats.IMessageBus>();
        var subscription = Substitute.For<Nats.ISubscription>();
        var context = Substitute.For<Nats.IMessageContext<string>>();
        context.Message.Returns("request");
        context.Subject.Returns("request.v1");
        context.Headers.Returns(new Nats.MessageHeaders(new() { ["trace"] = "abc" }));
        context.ReplyTo.Returns("reply.v1");
        bus.SubscribeAsync("request.v1", Arg.Any<Func<Nats.IMessageContext<string>, Task>>(), "workers", default)
            .Returns(async call =>
            {
                await call.Arg<Func<Nats.IMessageContext<string>, Task>>()(context);
                return subscription;
            });
        var transport = new NatsApplicationTransport(bus, Substitute.For<Nats.IJetStreamPublisher>(), Substitute.For<Nats.IJetStreamConsumer>());
        var handle = await transport.SubscribeAsync<string>("request.v1", async message =>
        {
            message.Message.ShouldBe("request");
            message.Subject.ShouldBe("request.v1");
            message.ReplyTo.ShouldBe("reply.v1");
            message.Headers.Headers["trace"].ShouldBe("abc");
            await message.RespondAsync("response");
        }, "workers");
        await context.Received(1).RespondAsync("response");
        await handle.StopAsync();
        await handle.DisposeAsync();
        await subscription.Received(1).StopAsync();
        await subscription.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Staged_Objects_Forward_Keys_Streams_And_Cancellation()
    {
        var store = Substitute.For<Nats.IObjectStore>();
        var adapter = new NatsStagedObjectStore(store);
        using var stream = new MemoryStream([1, 2, 3]);
        using var cts = new CancellationTokenSource();
        store.PutAsync("manifest", stream, cts.Token).Returns("manifest");
        (await adapter.PutAsync("manifest", stream, cts.Token)).ShouldBe("manifest");
        await adapter.GetAsync("manifest", stream, cts.Token);
        await adapter.DeleteAsync("manifest", cts.Token);
        await adapter.DisposeAsync();
        await store.Received(1).GetAsync("manifest", stream, cts.Token);
        await store.Received(1).DeleteAsync("manifest", cts.Token);
        await store.Received(1).DisposeAsync();
    }

    [Test]
    public void Contracts_Assembly_Does_Not_Reference_Nats()
    {
        typeof(IMessageBus).Assembly.GetReferencedAssemblies().ShouldNotContain(a => a.Name!.Contains("NATS", StringComparison.OrdinalIgnoreCase));
    }
}
