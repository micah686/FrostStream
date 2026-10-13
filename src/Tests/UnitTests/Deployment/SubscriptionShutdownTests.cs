using FrostStream.ApplicationContracts;
using NSubstitute;
using Shared.Messaging;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class SubscriptionShutdownTests
{
    private sealed class RegisteringService(IMessageBus bus, TaskCompletionSource firstRegistered,
        Task continueRegistration) : SubscriptionBackgroundService
    {
        protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
        {
            await SubscribeAsync<int>(bus, "first", _ => Task.CompletedTask);
            firstRegistered.TrySetResult();
            // Model a broker completing an in-flight registration while StopAsync runs.
            await continueRegistration;
            await SubscribeAsync<int>(bus, "second", _ => Task.CompletedTask);
        }
    }

    [Test]
    public async Task Concurrent_Shutdown_Disposes_Each_Subscription_Once()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bus = Substitute.For<IMessageBus>();
        var first = Substitute.For<ISubscription>();
        var second = Substitute.For<ISubscription>();
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StopAsync(Arg.Any<CancellationToken>()).Returns(releaseStop.Task);
        bus.SubscribeAsync<int>("first", Arg.Any<Func<IMessageContext<int>, Task>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(first);
        bus.SubscribeAsync<int>("second", Arg.Any<Func<IMessageContext<int>, Task>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(second);
        using var service = new RegisteringService(bus, new TaskCompletionSource(), Task.CompletedTask);
        await service.StartAsync(timeout.Token);
        await service.RegistrationCompleted.WaitAsync(timeout.Token);
        var firstStop = service.StopAsync(timeout.Token);
        var secondStop = service.StopAsync(timeout.Token);
        releaseStop.TrySetResult();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(timeout.Token);
        await first.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await second.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await first.Received(1).DisposeAsync();
        await second.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Shutdown_Waits_For_In_Flight_Registration_Before_Disposing_All_Subscriptions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bus = Substitute.For<IMessageBus>();
        var first = Substitute.For<ISubscription>();
        var second = Substitute.For<ISubscription>();
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StopAsync(Arg.Any<CancellationToken>()).Returns(releaseStop.Task);
        bus.SubscribeAsync<int>("first", Arg.Any<Func<IMessageContext<int>, Task>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(first);
        bus.SubscribeAsync<int>("second", Arg.Any<Func<IMessageContext<int>, Task>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(second);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new RegisteringService(bus, registered, continueRegistration.Task);
        await service.StartAsync(timeout.Token);
        await registered.Task.WaitAsync(timeout.Token);
        var stopping = service.StopAsync(timeout.Token);
        continueRegistration.TrySetResult();
        await service.RegistrationCompleted.WaitAsync(timeout.Token);
        releaseStop.TrySetResult();
        await stopping.WaitAsync(timeout.Token);
        await first.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await second.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await first.Received(1).DisposeAsync();
        await second.Received(1).DisposeAsync();
    }
}
