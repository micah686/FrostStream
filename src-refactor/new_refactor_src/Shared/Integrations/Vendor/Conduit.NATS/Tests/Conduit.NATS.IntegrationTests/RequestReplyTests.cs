using Conduit.NATS.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.IntegrationTests;

[ClassDataSource<NatsFixture>(Shared = SharedType.PerTestSession)]
public sealed class RequestReplyTests
{
    private readonly NatsFixture _nats;

    public RequestReplyTests(NatsFixture nats) => _nats = nats;

    [Test]
    [Timeout(60_000)]
    public async Task Request_Gets_Reply_From_Responder(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var bus = sp.GetRequiredService<IMessageBus>();

        // SubscribeAsync establishes the subscription before returning, so the responder is live
        // up front and a single request (same connection → ordered) gets answered.
        await using var sub = await bus.SubscribeAsync<PingMsg>(
            "rpc.ping",
            async ctx => await ctx.RespondAsync(new PongMsg($"{ctx.Message.Value}-pong")),
            cancellationToken: cancellationToken);

        var response = await bus.RequestAsync<PingMsg, PongMsg>("rpc.ping", new PingMsg("hi"), TimeSpan.FromSeconds(10), cancellationToken);

        response.ShouldNotBeNull();
        response!.Value.ShouldBe("hi-pong");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Request_With_No_Responder_Throws(CancellationToken cancellationToken)
    {
        await using var sp = _nats.BuildProvider();
        var bus = sp.GetRequiredService<IMessageBus>();

        // No responder must surface as an exception (not a null result) so callers can retry on it.
        await Should.ThrowAsync<NatsNoRespondersException>(async () =>
            await bus.RequestAsync<PingMsg, PongMsg>("rpc.no.responder", new PingMsg("x"), TimeSpan.FromSeconds(2), cancellationToken));
    }
}
