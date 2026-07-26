using Conduit.NATS;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.BackgroundJobs;

namespace UnitTests.WebAPI;

/// <summary>
/// Covers the queued → running handoff: the Scheduler announces a firing before any service has the
/// work, and the executing service's events have to land on that same row instead of opening a
/// second one.
/// </summary>
public sealed class BackgroundJobHubTests
{
    private const string IdempotencyKey = "search_reindex:weekly-search-reindex:2026-07-26T05:30:00Z";
    private static readonly Instant FiredAt = Instant.FromUtc(2026, 7, 26, 5, 30);

    [Test]
    public async Task Dispatch_Opens_A_Queued_Run_Before_Any_Service_Picks_It_Up()
    {
        var harness = await HubHarness.StartAsync();

        await harness.DispatchAsync();

        var run = harness.Hub.List().ShouldHaveSingleItem();
        run.Status.ShouldBe(BackgroundRunView.Queued);
        run.TaskType.ShouldBe("search_reindex");
        run.ScheduleKey.ShouldBe("weekly-search-reindex");
        run.Origin.ShouldBe("scheduler");
        run.QueuedAt.ShouldBe(FiredAt);

        await harness.StopAsync();
    }

    [Test]
    public async Task Start_Adopts_The_Queued_Run_Rather_Than_Adding_A_Second_Row()
    {
        var harness = await HubHarness.StartAsync();

        await harness.DispatchAsync();
        await harness.StartedAsync(origin: "databridge");

        var run = harness.Hub.List().ShouldHaveSingleItem();
        run.Status.ShouldBe(BackgroundRunView.Running);
        run.Origin.ShouldBe("databridge");
        // The wait before pickup stays on the row, so the queued history is not lost on start.
        run.QueuedAt.ShouldBe(FiredAt);
        run.Log.Count.ShouldBe(2);

        await harness.StopAsync();
    }

    [Test]
    public async Task Progress_And_Completion_Reach_A_Run_That_Only_The_Scheduler_Announced()
    {
        var harness = await HubHarness.StartAsync();

        // No start event: this is the executor reporting straight onto the queued row.
        await harness.DispatchAsync();
        await harness.ProgressAsync("Rebuilding the index…", current: 1, total: 4);
        await harness.CompletedAsync(success: true, summary: "Search index rebuilt.");

        var run = harness.Hub.List().ShouldHaveSingleItem();
        run.Status.ShouldBe(BackgroundRunView.Completed);
        run.Summary.ShouldBe("Search index rebuilt.");
        run.Percent.ShouldBe(100);

        await harness.StopAsync();
    }

    [Test]
    public async Task A_Repeated_Dispatch_Does_Not_Reset_A_Run_Already_Under_Way()
    {
        var harness = await HubHarness.StartAsync();

        await harness.DispatchAsync();
        await harness.StartedAsync(origin: "databridge");
        await harness.DispatchAsync();

        var run = harness.Hub.List().ShouldHaveSingleItem();
        run.Status.ShouldBe(BackgroundRunView.Running);
        run.Origin.ShouldBe("databridge");

        await harness.StopAsync();
    }

    [Test]
    public async Task Running_Runs_Sort_Above_Queued_Ones()
    {
        var harness = await HubHarness.StartAsync();

        await harness.DispatchAsync("backup:nightly-backup:2026-07-26T02:00:00Z", "backup", "nightly-backup");
        await harness.DispatchAsync();
        await harness.StartedAsync(origin: "databridge");

        var runs = harness.Hub.List();
        runs.Count.ShouldBe(2);
        runs[0].Status.ShouldBe(BackgroundRunView.Running);
        runs[1].TaskType.ShouldBe("backup");

        await harness.StopAsync();
    }

    /// <summary>
    /// Boots the hub against a substituted bus and keeps hold of the handlers it subscribed, so a
    /// test can feed it events the way NATS would.
    /// </summary>
    private sealed class HubHarness
    {
        private Func<IMessageContext<BackgroundRunDispatched>, Task> _dispatched = null!;
        private Func<IMessageContext<BackgroundRunStarted>, Task> _started = null!;
        private Func<IMessageContext<BackgroundRunProgress>, Task> _progress = null!;
        private Func<IMessageContext<BackgroundRunCompleted>, Task> _completed = null!;

        private HubHarness(BackgroundJobHub hub) => Hub = hub;

        public BackgroundJobHub Hub { get; }

        public static async Task<HubHarness> StartAsync()
        {
            var bus = Substitute.For<IMessageBus>();
            // Pinned to the firing instant: on the real clock the hub would prune these runs as
            // stale before a single assertion ran.
            var harness = new HubHarness(new BackgroundJobHub(
                bus, new FixedClock(FiredAt), NullLogger<BackgroundJobHub>.Instance));

            await harness.Hub.StartAsync(CancellationToken.None);

            // The host does not promise ExecuteAsync has run by the time StartAsync returns, so wait
            // for the hub to register all four subscriptions before feeding it anything.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!harness.Capture(bus) && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            if (harness._dispatched is null)
                throw new InvalidOperationException("The hub did not subscribe to background run telemetry.");

            return harness;
        }

        /// <summary>Reads the handlers back off the recorded subscribe calls: (subject, handler, …).</summary>
        private bool Capture(IMessageBus bus)
        {
            foreach (var call in bus.ReceivedCalls())
            {
                var arguments = call.GetArguments();
                if (arguments.Length < 2 || arguments[0] is not string subject)
                    continue;

                switch (subject)
                {
                    case BackgroundRunSubjects.Dispatched:
                        _dispatched = (Func<IMessageContext<BackgroundRunDispatched>, Task>)arguments[1]!;
                        break;
                    case BackgroundRunSubjects.Started:
                        _started = (Func<IMessageContext<BackgroundRunStarted>, Task>)arguments[1]!;
                        break;
                    case BackgroundRunSubjects.Progress:
                        _progress = (Func<IMessageContext<BackgroundRunProgress>, Task>)arguments[1]!;
                        break;
                    case BackgroundRunSubjects.Completed:
                        _completed = (Func<IMessageContext<BackgroundRunCompleted>, Task>)arguments[1]!;
                        break;
                }
            }

            return _dispatched is not null && _started is not null
                && _progress is not null && _completed is not null;
        }

        public Task StopAsync() => Hub.StopAsync(CancellationToken.None);

        public Task DispatchAsync(
            string idempotencyKey = IdempotencyKey,
            string taskType = "search_reindex",
            string scheduleKey = "weekly-search-reindex")
            => _dispatched(Context(new BackgroundRunDispatched
            {
                RunId = BackgroundRunIds.ForIdempotencyKey(idempotencyKey),
                TaskType = taskType,
                ScheduleKey = scheduleKey,
                Trigger = BackgroundRunTrigger.Scheduled,
                IdempotencyKey = idempotencyKey,
                Origin = "scheduler",
                DueWindowUtc = FiredAt,
                DispatchedAt = FiredAt
            }));

        public Task StartedAsync(string origin, string idempotencyKey = IdempotencyKey)
            => _started(Context(new BackgroundRunStarted
            {
                RunId = BackgroundRunIds.ForIdempotencyKey(idempotencyKey),
                TaskType = "search_reindex",
                ScheduleKey = "weekly-search-reindex",
                Trigger = BackgroundRunTrigger.Scheduled,
                IdempotencyKey = idempotencyKey,
                Origin = origin,
                StartedAt = FiredAt.Plus(Duration.FromSeconds(4))
            }));

        public Task ProgressAsync(string message, int current, int total)
            => _progress(Context(new BackgroundRunProgress
            {
                RunId = BackgroundRunIds.ForIdempotencyKey(IdempotencyKey),
                Message = message,
                Current = current,
                Total = total,
                OccurredAt = FiredAt.Plus(Duration.FromSeconds(10))
            }));

        public Task CompletedAsync(bool success, string summary)
            => _completed(Context(new BackgroundRunCompleted
            {
                RunId = BackgroundRunIds.ForIdempotencyKey(IdempotencyKey),
                Success = success,
                Summary = summary,
                CompletedAt = FiredAt.Plus(Duration.FromSeconds(30))
            }));

        private sealed class FixedClock(Instant now) : IClock
        {
            public Instant GetCurrentInstant() => now;
        }

        private static IMessageContext<T> Context<T>(T message)
        {
            var context = Substitute.For<IMessageContext<T>>();
            context.Message.Returns(message);
            return context;
        }
    }
}
