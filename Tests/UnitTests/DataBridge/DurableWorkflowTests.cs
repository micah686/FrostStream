using Conduit.NATS;
using DataBridge.Application;
using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Application;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class DurableWorkflowTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 11, 20, 0);

    [Test]
    public async Task Download_Ingress_Commits_Deduplication_After_Durable_Flow_Start()
    {
        var request = Request();
        var run = new DownloadRunRequest { RunId = Guid.NewGuid(), RunNumber = 1, Request = request };
        var legacy = Substitute.For<IDownloadJobsRepository>();
        var repository = Substitute.For<IDownloadFlowV2Repository>();
        var starter = Substitute.For<IDownloadWorkflowStarter>();
        var order = new List<string>();
        repository.CreateInitialRunAsync(request, true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("intent");
                return run;
            });
        starter.StartJobAsync(request.JobId, run, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("flow");
                return Task.CompletedTask;
            });
        legacy.MarkMessageProcessedAsync(
                request.MessageId, request.OperationKey, request.JobId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("dedupe");
                return Task.CompletedTask;
            });
        var ingress = new DownloadWorkflowIngress(legacy, repository, starter, ReadyStartupState());

        var receipt = await ingress.AcceptAsync(request);

        receipt.Disposition.ShouldBe(DurableWorkDisposition.Accepted);
        order.ShouldBe(["intent", "flow", "dedupe"]);
    }

    [Test]
    public async Task Download_Ingress_Duplicate_Does_Not_Start_Another_Flow()
    {
        var request = Request();
        var legacy = Substitute.For<IDownloadJobsRepository>();
        legacy.IsMessageProcessedAsync(request.MessageId, Arg.Any<CancellationToken>()).Returns(true);
        var repository = Substitute.For<IDownloadFlowV2Repository>();
        var starter = Substitute.For<IDownloadWorkflowStarter>();
        var ingress = new DownloadWorkflowIngress(legacy, repository, starter, ReadyStartupState());

        var receipt = await ingress.AcceptAsync(request);

        receipt.Disposition.ShouldBe(DurableWorkDisposition.Duplicate);
        await repository.DidNotReceiveWithAnyArgs().CreateInitialRunAsync(default!, default);
        await starter.DidNotReceiveWithAnyArgs().StartJobAsync(default, default!);
    }

    [Test]
    public async Task Full_Adapter_Acknowledges_Only_After_Durable_Acceptance_Completes()
    {
        var request = Request();
        var context = Substitute.For<IJsMessageContext<DownloadRequested>>();
        context.Message.Returns(request);
        var committed = new TaskCompletionSource<DurableWorkReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handling = JetStreamWorkAdapter.ExecuteAsync(
            context,
            (_, _) =>
            {
                started.SetResult();
                return committed.Task;
            },
            Substitute.For<ILogger>());

        await started.Task;
        await context.DidNotReceiveWithAnyArgs().AckAsync();
        committed.SetResult(new DurableWorkReceipt(
            DurableWorkDisposition.Accepted,
            request.JobId,
            request.MessageId.ToString("N")));
        await handling;

        await context.Received(1).AckAsync(Arg.Any<CancellationToken>());
        await context.DidNotReceiveWithAnyArgs().NackAsync();
    }

    [Test]
    public async Task Full_Adapter_Nacks_Failure_And_Leaves_Cancelled_Work_For_Redelivery()
    {
        var request = Request();
        var failed = Substitute.For<IJsMessageContext<DownloadRequested>>();
        failed.Message.Returns(request);
        await JetStreamWorkAdapter.ExecuteAsync(
            failed,
            (_, _) => throw new InvalidOperationException("commit failed"),
            Substitute.For<ILogger>());

        await failed.Received(1).NackAsync(null, Arg.Any<CancellationToken>());
        await failed.DidNotReceiveWithAnyArgs().AckAsync();

        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var cancelled = Substitute.For<IJsMessageContext<DownloadRequested>>();
        cancelled.Message.Returns(request);
        await JetStreamWorkAdapter.ExecuteAsync(
            cancelled,
            (_, ct) => Task.FromCanceled<DurableWorkReceipt>(ct),
            Substitute.For<ILogger>(),
            shutdown.Token);

        await cancelled.DidNotReceiveWithAnyArgs().AckAsync();
        await cancelled.DidNotReceiveWithAnyArgs().NackAsync();
    }

    [Test]
    public async Task Local_Progress_Is_Bounded_And_Reconnect_Loads_Persisted_Snapshot()
    {
        var snapshots = Substitute.For<IPersistedProgressSnapshotStore<string>>();
        snapshots.LoadAsync("job/1", Arg.Any<CancellationToken>()).Returns("persisted-running");
        var hub = new BoundedLocalProgressHub<int, string>(snapshots);
        await using var subscription = await hub.SubscribeAsync("job/1", capacity: 2);

        hub.Publish("other", 0);
        hub.Publish("job/1", 1);
        hub.Publish("job/1", 2);
        hub.Publish("job/1", 3);

        subscription.Snapshot.ShouldBe("persisted-running");
        (await subscription.Events.ReadAsync()).ShouldBe(2);
        (await subscription.Events.ReadAsync()).ShouldBe(3);
    }

    private static DownloadRequested Request()
        => new()
        {
            JobId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            OperationKey = $"download/{Guid.NewGuid():N}",
            OccurredAt = Now,
            SourceUrl = "https://fixture.invalid/video.mp4"
        };

    private static DownloadFlowStartupState ReadyStartupState()
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);
        var state = new DownloadFlowStartupState(clock);
        state.MarkReady();
        return state;
    }
}
