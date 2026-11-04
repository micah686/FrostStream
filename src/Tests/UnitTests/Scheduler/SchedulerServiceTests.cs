using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Quartz;
using Scheduler.Databridge;
using Scheduler.Scheduling;
using Scheduler.Services;
using Scheduler.Triggers;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Scheduler;

public sealed class SchedulerServiceTests
{
    [Test]
    public async Task Hydration_Handles_Failures_Retries_And_Registers_Active_Definitions()
    {
        var scheduler = Substitute.For<IScheduler>();
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);
        var client = Substitute.For<IDatabridgeClient>();
        var registrar = Substitute.For<IQuartzJobRegistrar>();
        registrar.RegisterAsync(scheduler, Arg.Any<ScheduledTaskDto>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var hydration = new ScheduleHydrationService(client, factory, registrar, NullLogger<ScheduleHydrationService>.Instance);

        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns((ScheduleOperationResponseMessage?)null);
        (await hydration.HydrateAsync(CancellationToken.None)).ShouldBeFalse();
        await factory.DidNotReceive().GetScheduler(Arg.Any<CancellationToken>());

        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage { Success = false, ErrorMessage = "unavailable" });
        (await hydration.HydrateAsync(CancellationToken.None)).ShouldBeFalse();

        var definition = Definition("nightly");
        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage
        {
            Success = true,
            Items = [definition]
        });
        (await hydration.HydrateAsync(CancellationToken.None)).ShouldBeTrue();
        await registrar.Received(1).RegisterAsync(scheduler, definition, Arg.Any<CancellationToken>());

        var calls = 0;
        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            if (calls == 1) throw new IOException("temporary");
            return Task.FromResult<ScheduleOperationResponseMessage?>(new ScheduleOperationResponseMessage { Success = true, Items = [] });
        });
        (await hydration.HydrateAsync(CancellationToken.None)).ShouldBeTrue();
        calls.ShouldBe(2);
    }

    [Test]
    public async Task Hydration_Returns_False_After_The_Final_Retry_And_BackgroundService_Executes_Hydration()
    {
        var client = Substitute.For<IDatabridgeClient>();
        var scheduler = Substitute.For<IScheduler>();
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);
        var registrar = Substitute.For<IQuartzJobRegistrar>();
        var attempts = 0;
        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            attempts++;
            return Task.FromException<ScheduleOperationResponseMessage?>(new IOException("unavailable"));
        });
        var retrying = new ScheduleHydrationService(client, factory, registrar, NullLogger<ScheduleHydrationService>.Instance)
        {
            DelayAsync = (_, _) => Task.CompletedTask
        };

        (await retrying.HydrateAsync(CancellationToken.None)).ShouldBeFalse();
        attempts.ShouldBe(7);

        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage { Success = true, Items = [] });
        var hosted = new ScheduleHydrationService(client, factory, registrar, NullLogger<ScheduleHydrationService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await hosted.StartAsync(timeout.Token);
        await hosted.ExecuteTask!;
        await hosted.StopAsync(timeout.Token);
    }

    [Test]
    public async Task ScheduleChangeListener_Deletes_Failed_Disabled_And_Deleted_Changes_And_Registers_Valid_Changes()
    {
        var bus = new RecordingBus();
        var client = Substitute.For<IDatabridgeClient>();
        var scheduler = Substitute.For<IScheduler>();
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);
        var registrar = Substitute.For<IQuartzJobRegistrar>();
        registrar.RegisterAsync(scheduler, Arg.Any<ScheduledTaskDto>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        registrar.DeleteAsync(scheduler, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var listener = new ScheduleChangeListener(bus, client, factory, registrar, NullLogger<ScheduleChangeListener>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await listener.StartAsync(stop.Token);
        await listener.RegistrationCompleted.WaitAsync(stop.Token);
        var handler = bus.ScheduleChangedHandler.ShouldNotBeNull();

        await handler(Message(ScheduleChangeKind.Deleted, "deleted"));
        await registrar.Received(1).DeleteAsync(scheduler, "deleted", Arg.Any<CancellationToken>());

        client.GetScheduleAsync("missing", Arg.Any<CancellationToken>()).Returns((ScheduleOperationResponseMessage?)null);
        await handler(Message(ScheduleChangeKind.Updated, "missing"));
        await registrar.Received(1).DeleteAsync(scheduler, "missing", Arg.Any<CancellationToken>());

        client.GetScheduleAsync("disabled", Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage
        {
            Success = true,
            Entity = Definition("disabled", enabled: false)
        });
        await handler(Message(ScheduleChangeKind.Updated, "disabled"));
        await registrar.Received(1).DeleteAsync(scheduler, "disabled", Arg.Any<CancellationToken>());

        client.GetScheduleAsync("unknown", Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage
        {
            Success = true,
            Entity = Definition("unknown", taskType: "not-registered")
        });
        await handler(Message(ScheduleChangeKind.Updated, "unknown"));
        await registrar.DidNotReceive().RegisterAsync(scheduler, Arg.Is<ScheduledTaskDto>(x => x.Key == "unknown"), Arg.Any<CancellationToken>());

        var valid = Definition("valid", TaskTypeRegistry.DatabaseMaintenance);
        client.GetScheduleAsync("valid", Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage { Success = true, Entity = valid });
        await handler(Message(ScheduleChangeKind.Created, "valid"));
        await registrar.Received(1).RegisterAsync(scheduler, valid, Arg.Any<CancellationToken>());

        await listener.StopAsync(CancellationToken.None);
        bus.Subscription.StopCount.ShouldBe(1);
        bus.Subscription.DisposeCount.ShouldBe(1);
    }

    private static IMessageContext<ScheduleChangedMessage> Message(ScheduleChangeKind kind, string key)
    {
        var context = Substitute.For<IMessageContext<ScheduleChangedMessage>>();
        context.Message.Returns(new ScheduleChangedMessage { Key = key, Kind = kind, OccurredAt = Instant.FromUtc(2026, 1, 1, 0, 0) });
        return context;
    }

    private static ScheduledTaskDto Definition(string key, string taskType = TaskTypeRegistry.DatabaseMaintenance, bool enabled = true) => new()
    {
        Id = 1,
        Key = key,
        TaskType = taskType,
        Timezone = "UTC",
        Enabled = enabled,
        CatchupPolicy = ScheduleCatchupPolicy.Coalesce,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
    };

    private sealed class RecordingBus : IMessageBus
    {
        public Func<IMessageContext<ScheduleChangedMessage>, Task>? ScheduleChangedHandler { get; private set; }
        public RecordingSubscription Subscription { get; } = new();

        public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<TResponse?>(default);

        public Task<ISubscription> SubscribeAsync<T>(string subject, Func<IMessageContext<T>, Task> handler,
            string? queueGroup = null, CancellationToken cancellationToken = default)
        {
            if (typeof(T) == typeof(ScheduleChangedMessage))
            {
                ScheduleChangedHandler = context => handler((IMessageContext<T>)(object)context);
            }

            return Task.FromResult<ISubscription>(Subscription);
        }
    }

    private sealed class RecordingSubscription : ISubscription
    {
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public Guid Id { get; } = Guid.NewGuid();
    }
}
