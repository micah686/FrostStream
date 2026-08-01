using Conduit.NATS;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

public sealed class TopologyReadySignalTests
{
    [Test]
    public async Task SignalReady_Completes_Waiters()
    {
        var signal = new TopologyReadySignal(NullLogger<TopologyReadySignal>.Instance);
        var wait = signal.WaitAsync();

        signal.SignalReady();

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        signal.IsSignaled.ShouldBeTrue();
    }

    [Test]
    public async Task SignalFailed_Propagates_To_Waiters()
    {
        var signal = new TopologyReadySignal(NullLogger<TopologyReadySignal>.Instance);
        var wait = signal.WaitAsync();

        signal.SignalFailed(new InvalidOperationException("boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await wait.WaitAsync(TimeSpan.FromSeconds(1)));
        ex.Message.ShouldBe("boom");
    }

    [Test]
    public async Task First_Signal_Wins()
    {
        var signal = new TopologyReadySignal(NullLogger<TopologyReadySignal>.Instance);
        signal.SignalReady();
        signal.SignalFailed(new InvalidOperationException("late"));

        await signal.WaitAsync().WaitAsync(TimeSpan.FromSeconds(1)); // still completes successfully
        signal.IsSignaled.ShouldBeTrue();
    }
}
