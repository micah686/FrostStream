using DataBridge.Initialization;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class ApplicationInitializationCoordinatorTests
{
    [Test]
    public async Task Executes_Dependencies_Then_Records_Overall_Success()
    {
        var events = new List<string>();
        var state = new RecordingStateStore(events);
        var coordinator = Create(
            state,
            new FakeStep("collections", ["database"], events),
            new FakeStep("database", [], events));

        await coordinator.InitializeAsync(CancellationToken.None);

        events.ShouldBe(["clear", "database", "collections", "success:1"]);
    }

    [Test]
    public async Task Mid_Initialization_Failure_Records_No_Overall_Success_And_Retry_Is_Safe()
    {
        var events = new List<string>();
        var state = new RecordingStateStore(events);
        await state.RecordSuccessAsync(1, CancellationToken.None);
        events.Clear();
        var failing = new FakeStep("external", ["database"], events, failuresRemaining: 1);
        var steps = new IApplicationInitializationStep[]
        {
            failing,
            new FakeStep("database", [], events)
        };
        var coordinator = Create(state, steps);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => coordinator.InitializeAsync(CancellationToken.None));
        exception.Message.ShouldContain("external");
        state.Version.ShouldBeNull();

        await coordinator.InitializeAsync(CancellationToken.None);

        state.Version.ShouldBe(ApplicationInitializationCoordinator.CurrentVersion);
        events.Count(entry => entry == "clear").ShouldBe(2);
        events.Count(entry => entry == "database").ShouldBe(2);
        events.Count(entry => entry == "external").ShouldBe(2);
        events.Last().ShouldBe("success:1");
    }

    [Test]
    public async Task Step_Deadline_Is_Reported_With_The_Step_Name()
    {
        var events = new List<string>();
        var coordinator = Create(
            new RecordingStateStore(events),
            new HangingStep());

        var exception = await Should.ThrowAsync<TimeoutException>(
            () => coordinator.InitializeAsync(CancellationToken.None));

        exception.Message.ShouldContain("slow-service");
    }

    [Test]
    public async Task Missing_Or_Cyclic_Dependency_Fails_Before_Any_Step_Runs()
    {
        var events = new List<string>();
        var state = new RecordingStateStore(events);
        var coordinator = Create(state, new FakeStep("one", ["missing"], events));

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => coordinator.InitializeAsync(CancellationToken.None));

        exception.Message.ShouldContain("missing or cyclic");
        events.ShouldBeEmpty();
    }

    private static ApplicationInitializationCoordinator Create(
        IInitializationStateStore state,
        params IApplicationInitializationStep[] steps) => new(
        steps,
        state,
        NullLogger<ApplicationInitializationCoordinator>.Instance);

    private sealed class FakeStep(
        string name,
        IReadOnlyCollection<string> dependencies,
        List<string> events,
        int failuresRemaining = 0) : IApplicationInitializationStep
    {
        private int _failuresRemaining = failuresRemaining;
        public string Name => name;
        public IReadOnlyCollection<string> Dependencies => dependencies;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            events.Add(name);
            if (_failuresRemaining-- > 0)
                throw new IOException("injected failure");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStateStore(List<string> events) : IInitializationStateStore
    {
        public int? Version { get; private set; }
        public Task<int?> ReadVersionAsync(CancellationToken cancellationToken) => Task.FromResult(Version);

        public Task ClearSuccessAsync(CancellationToken cancellationToken)
        {
            Version = null;
            events.Add("clear");
            return Task.CompletedTask;
        }

        public Task RecordSuccessAsync(int version, CancellationToken cancellationToken)
        {
            Version = version;
            events.Add($"success:{version}");
            return Task.CompletedTask;
        }
    }

    private sealed class HangingStep : IApplicationInitializationStep
    {
        public string Name => "slow-service";
        public IReadOnlyCollection<string> Dependencies => [];
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(20);
        public Task ExecuteAsync(CancellationToken cancellationToken) =>
            Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
