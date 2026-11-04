using FrostStream.ApplicationContracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Quartz;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using global::Scheduler;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Scheduler;

public sealed class SchedulerListenerTests
{
    [Test]
    public async Task DispatchListener_Announces_Firings_And_Closes_Out_Vetoes_And_Failures()
    {
        var bus = new CapturingMessageBus();
        var now = Instant.FromUtc(2026, 8, 3, 2, 0);
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(now);
        var listener = new BackgroundRunDispatchListener(bus, clock, NullLogger<BackgroundRunDispatchListener>.Instance);
        var context = JobContext();

        await listener.JobToBeExecuted(context, CancellationToken.None);
        bus.Messages.Count.ShouldBe(1);
        bus.Messages[0].Subject.ShouldBe(BackgroundRunSubjects.Dispatched);
        var dispatched = bus.Messages[0].Message.ShouldBeOfType<BackgroundRunDispatched>();
        dispatched.TaskType.ShouldBe(TaskTypeRegistry.DatabaseMaintenance);
        dispatched.ScheduleKey.ShouldBe("scheduled-key");
        dispatched.IdempotencyKey.ShouldBe("db-maintenance:scheduled-key:2026-08-03T02:00:00Z");
        dispatched.Origin.ShouldBe("scheduler");
        dispatched.DueWindowUtc.ShouldBe(now);

        await listener.JobExecutionVetoed(context, CancellationToken.None);
        var veto = bus.Messages[1].Message.ShouldBeOfType<BackgroundRunCompleted>();
        veto.Success.ShouldBeFalse();
        veto.ErrorMessage!.ShouldContain("vetoed");

        await listener.JobWasExecuted(context, new JobExecutionException("service unavailable"), CancellationToken.None);
        var failure = bus.Messages[2].Message.ShouldBeOfType<BackgroundRunCompleted>();
        failure.Success.ShouldBeFalse();
        failure.ErrorMessage!.ShouldContain("service unavailable");

        await listener.JobWasExecuted(context, null, CancellationToken.None);
        bus.Messages.Count.ShouldBe(3);

        var invalidContext = JobContext(valid: false);
        await listener.JobToBeExecuted(invalidContext, CancellationToken.None);
        await listener.JobExecutionVetoed(invalidContext, CancellationToken.None);
        await listener.JobWasExecuted(invalidContext, new JobExecutionException("ignored"), CancellationToken.None);
        bus.Messages.Count.ShouldBe(3);
    }

    [Test]
    public async Task DispatchListener_Does_Not_Fail_Scheduled_Work_When_Telemetry_Publish_Fails()
    {
        var bus = new CapturingMessageBus { FailPublishes = true };
        var listener = new BackgroundRunDispatchListener(bus, Substitute.For<IClock>(), NullLogger<BackgroundRunDispatchListener>.Instance);

        await Should.NotThrowAsync(() => listener.JobToBeExecuted(JobContext(), CancellationToken.None).AsTask());
        await Should.NotThrowAsync(() => listener.JobExecutionVetoed(JobContext(), CancellationToken.None).AsTask());
        await Should.NotThrowAsync(() => listener.JobWasExecuted(JobContext(), new JobExecutionException("failure"), CancellationToken.None).AsTask());
    }

    [Test]
    public void SchedulerOptions_Expose_Defaults_And_Configurable_Values()
    {
        new global::Scheduler.Options.ChannelJobOptions().Enabled.ShouldBeTrue();
        new global::Scheduler.Options.MaintenanceJobOptions().Enabled.ShouldBeTrue();
        new global::Scheduler.Options.SchedulerQuartzOptions().MaxConcurrency.ShouldBe(10);

        var dashboard = new global::Scheduler.Options.QuartzDashboardOptions
        {
            Path = "jobs",
            ReadOnly = true,
            LazyInit = true,
            TimelineSpanMinutes = 15
        };
        dashboard.Path.ShouldBe("jobs");
        dashboard.ReadOnly.ShouldBeTrue();
        dashboard.LazyInit.ShouldBeTrue();
        dashboard.TimelineSpanMinutes.ShouldBe(15);
    }

    [Test]
    public void DashboardMapping_Accepts_Default_Relative_And_Rooted_Paths()
    {
        foreach (var path in new string?[] { null, "", "jobs", "/jobs" })
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton<IOptions<global::Scheduler.Options.QuartzDashboardOptions>>(
                Options.Create(new global::Scheduler.Options.QuartzDashboardOptions { Path = path! }));
            var schedulerFactory = Substitute.For<ISchedulerFactory>();
            schedulerFactory.GetScheduler().Returns(ValueTask.FromResult(Substitute.For<IScheduler>()));
            builder.Services.AddSingleton(schedulerFactory);
            using var app = builder.Build();

            app.MapSchedulerModule().ShouldBeSameAs(app);
        }
    }

    [Test]
    public void Dashboard_Options_And_Callbacks_Can_Be_Exercised_Without_Starting_A_Web_Server()
    {
        var scheduler = Substitute.For<IScheduler>();
        var schedulerProvider = CrystalQuartzSetup.CreateSchedulerProvider(scheduler);
        schedulerProvider().ShouldBe(scheduler);

        var logger = Substitute.For<ILogger>();
        var options = CrystalQuartzSetup.CreateOptions(new global::Scheduler.Options.QuartzDashboardOptions
        {
            Path = "jobs",
            ReadOnly = true,
            LazyInit = true,
            TimelineSpanMinutes = 0
        }, logger);
        options.Path.ShouldBe("/jobs");
        options.ReadOnly.ShouldBeTrue();
        options.LazyInit.ShouldBeTrue();
        options.TimelineSpan.ShouldBe(TimeSpan.FromMinutes(1));

        var exception = new InvalidOperationException("panel failed");
        CrystalQuartzSetup.CreatePanelExceptionHandler(logger)(exception);
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log");
    }

    private static IJobExecutionContext JobContext(bool valid = true)
    {
        var map = new JobDataMap();
        if (valid)
        {
            map[QuartzScheduleFactory.ScheduleKeyData] = "scheduled-key";
            map[QuartzScheduleFactory.TaskTypeData] = TaskTypeRegistry.DatabaseMaintenance;
            map[QuartzScheduleFactory.RetentionDaysData] = 7;
            map[QuartzScheduleFactory.IncludeFailedData] = false;
        }

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey("scheduled-key", "scheduled-tasks"));
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(map);
        context.JobDetail.Returns(jobDetail);
        context.ScheduledFireTimeUtc.Returns(new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero));
        return context;
    }

    private sealed class CapturingMessageBus : IMessageBus
    {
        public List<(string Subject, object Message)> Messages { get; } = [];
        public bool FailPublishes { get; init; }

        public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        {
            if (FailPublishes) throw new IOException("bus unavailable");
            Messages.Add((subject, message!));
            return Task.CompletedTask;
        }

        public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
            => PublishAsync(subject, message, cancellationToken);

        public Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult<TResponse?>(default);

        public Task<ISubscription> SubscribeAsync<T>(string subject, Func<IMessageContext<T>, Task> handler, string? queueGroup = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
