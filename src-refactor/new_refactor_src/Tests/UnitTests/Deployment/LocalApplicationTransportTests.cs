using Microsoft.Extensions.Logging.Abstractions;
using Shared.Messaging.Adapters;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Deployment;

public sealed class LocalApplicationTransportTests
{
    [Test]
    public async Task Requests_Use_Existing_Handlers_And_Support_Nested_Requests()
    {
        await using var bus = new LocalApplicationTransport(NullLogger<LocalApplicationTransport>.Instance);
        await using var inner = await bus.SubscribeAsync<int>("inner", c => c.RespondAsync(c.Message + 1));
        await using var outer = await bus.SubscribeAsync<int>("outer", async c =>
            await c.RespondAsync(await bus.RequestAsync<int, int>("inner", c.Message, TimeSpan.FromSeconds(2))));
        (await bus.RequestAsync<int, int>("outer", 4, TimeSpan.FromSeconds(2))).ShouldBe(5);
        (await bus.RequestAsync<string, string>("absent", "x", TimeSpan.FromMilliseconds(10))).ShouldBeNull();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => bus.RequestAsync<int, int>("outer", 4, TimeSpan.FromSeconds(2), cancellation.Token));
    }

    [Test]
    public async Task Progress_Fans_Out_And_Queue_Groups_Deliver_Once()
    {
        await using var bus = new LocalApplicationTransport(NullLogger<LocalApplicationTransport>.Instance);
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var grouped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var a = await bus.SubscribeAsync<int>("progress.*", c => { first.TrySetResult(c.Message); return Task.CompletedTask; });
        await using var b = await bus.SubscribeAsync<int>("progress.>", c => { second.TrySetResult(c.Message); return Task.CompletedTask; });
        await using var g1 = await bus.SubscribeAsync<int>("progress.run", c => { grouped.TrySetResult(Interlocked.Increment(ref count)); return Task.CompletedTask; }, "workers");
        await using var g2 = await bus.SubscribeAsync<int>("progress.run", c => { grouped.TrySetResult(Interlocked.Increment(ref count)); return Task.CompletedTask; }, "workers");
        await bus.PublishAsync("progress.run", 42);
        (await first.Task.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(42);
        (await second.Task.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(42);
        (await grouped.Task.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(1);
        await g1.StopAsync();
        await g2.StopAsync();
        count.ShouldBe(1);
    }

    [Test]
    public async Task Full_Queue_Blocks_And_Cancellation_And_Stop_Release_Publishers()
    {
        await using var bus = new LocalApplicationTransport(NullLogger<LocalApplicationTransport>.Instance, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await bus.SubscribeAsync<int>("progress", async _ => { entered.TrySetResult(); await release.Task; });
        await bus.PublishAsync("progress", 1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await bus.PublishAsync("progress", 2);
        using var cancellation = new CancellationTokenSource();
        var blocked = bus.PublishAsync("progress", 3, cancellation.Token);
        blocked.IsCompleted.ShouldBeFalse();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => blocked);
        var stoppedPublisher = bus.PublishAsync("progress", 4);
        stoppedPublisher.IsCompleted.ShouldBeFalse();
        var stop = subscription.StopAsync();
        await stoppedPublisher.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
