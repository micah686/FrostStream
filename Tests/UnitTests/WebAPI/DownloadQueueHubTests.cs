using System.Text;
using System.Threading.Channels;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Downloads;
using WebAPI.Features.Downloads.Controllers;

namespace UnitTests.WebAPI;

public sealed class DownloadQueueHubTests
{
    [Test]
    public async Task Sse_Serializes_Progress_And_State_As_Named_Events()
    {
        var hub = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var body = new MemoryStream();
            var controller = new DownloadQueueController(
                Substitute.For<IMessageBus>(), hub.Hub, Substitute.For<ILogger<DownloadQueueController>>());
            var httpContext = new DefaultHttpContext();
            httpContext.Response.Body = body;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var streaming = controller.StreamJobProgressAsync(jobId, cts.Token);

            await hub.DispatchProgress(Progress(jobId, sequence: 3));
            await hub.DispatchState(StateChange(jobId, DownloadJobState.Uploaded, DownloadJobState.Completed));

            for (var i = 0; i < 200 && body.Length == 0; i++)
                await Task.Delay(10, CancellationToken.None);
            // Give the second event time to flush too.
            await Task.Delay(50, CancellationToken.None);

            await cts.CancelAsync();
            try { await streaming; } catch (OperationCanceledException) { }

            var text = Encoding.UTF8.GetString(body.ToArray());
            text.ShouldContain("event: progress");
            text.ShouldContain($"\"jobId\":\"{jobId}\"");
            text.ShouldContain("\"sequence\":3");
            text.ShouldContain("\"phase\":\"Downloading\"");
            text.ShouldContain("event: state");
            text.ShouldContain("\"state\":\"Completed\"");
            text.ShouldContain("\"previousState\":\"Uploaded\"");
        }
        finally
        {
            await hub.Hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Queue_Subscriber_Receives_All_Jobs_And_Job_Subscriber_Is_Filtered()
    {
        var hub = await StartHubAsync();
        try
        {
            var jobA = Guid.NewGuid();
            var jobB = Guid.NewGuid();

            var (_, queueReader) = hub.Hub.SubscribeToQueue();
            var (_, jobReader) = hub.Hub.SubscribeToJob(jobA);

            await hub.DispatchProgress(Progress(jobA, sequence: 1));
            await hub.DispatchProgress(Progress(jobB, sequence: 2));

            (await ReadProgress(queueReader)).JobId.ShouldBe(jobA);
            (await ReadProgress(queueReader)).JobId.ShouldBe(jobB);

            var forJob = await ReadProgress(jobReader);
            forJob.JobId.ShouldBe(jobA);
            forJob.Sequence.ShouldBe(1);
            (await TryRead(jobReader)).ShouldBeNull();
        }
        finally
        {
            await hub.Hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task State_Events_Reach_Queue_And_Job_Subscribers()
    {
        var hub = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var (_, queueReader) = hub.Hub.SubscribeToQueue();
            var (_, jobReader) = hub.Hub.SubscribeToJob(jobId);

            await hub.DispatchState(StateChange(jobId, DownloadJobState.DownloadPending, DownloadJobState.DownloadQueued));

            (await ReadState(queueReader)).State.ShouldBe(DownloadJobState.DownloadQueued);
            (await ReadState(jobReader)).PreviousState.ShouldBe(DownloadJobState.DownloadPending);
        }
        finally
        {
            await hub.Hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Progress_Is_Coalesced_But_Phase_Change_And_Final_Frame_Pass()
    {
        var hub = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var (_, reader) = hub.Hub.SubscribeToQueue();

            // First frame always passes; the next same-phase frame within the window is dropped.
            await hub.DispatchProgress(Progress(jobId, sequence: 1, phase: "Downloading", percent: 10));
            await hub.DispatchProgress(Progress(jobId, sequence: 2, phase: "Downloading", percent: 11));
            // Phase change always passes; the 100% frame always passes.
            await hub.DispatchProgress(Progress(jobId, sequence: 3, phase: "Merging", percent: 90));
            await hub.DispatchProgress(Progress(jobId, sequence: 4, phase: "Merging", percent: 100));

            var sequences = new List<int>();
            for (var i = 0; i < 4; i++)
            {
                var frame = await TryRead(reader);
                if (frame is null) break;
                sequences.Add(((QueueStreamEvent.Progress)frame).Value.Sequence);
            }

            sequences.ShouldContain(1);  // first
            sequences.ShouldNotContain(2); // coalesced away
            sequences.ShouldContain(3);  // phase change
            sequences.ShouldContain(4);  // final frame
        }
        finally
        {
            await hub.Hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Unsubscribed_Reader_Stops_Receiving()
    {
        var hub = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var (id, reader) = hub.Hub.SubscribeToJob(jobId);

            hub.Hub.Unsubscribe(id);
            await hub.DispatchProgress(Progress(jobId, sequence: 1));

            (await TryRead(reader)).ShouldBeNull();
        }
        finally
        {
            await hub.Hub.StopAsync(CancellationToken.None);
        }
    }

    private sealed record HubHarness(
        DownloadQueueHub Hub,
        Func<DownloadProgress, Task> DispatchProgress,
        Func<DownloadQueueStateChanged, Task> DispatchState);

    private static async Task<HubHarness> StartHubAsync()
    {
        var bus = Substitute.For<IMessageBus>();
        var subscription = Substitute.For<ISubscription>();
        Func<IMessageContext<DownloadProgress>, Task>? progressHandler = null;
        Func<IMessageContext<DownloadQueueStateChanged>, Task>? stateHandler = null;

        bus.SubscribeAsync(
                Arg.Any<string>(),
                Arg.Any<Func<IMessageContext<DownloadProgress>, Task>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => { progressHandler = ci.Arg<Func<IMessageContext<DownloadProgress>, Task>>(); return subscription; });
        bus.SubscribeAsync(
                Arg.Any<string>(),
                Arg.Any<Func<IMessageContext<DownloadQueueStateChanged>, Task>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => { stateHandler = ci.Arg<Func<IMessageContext<DownloadQueueStateChanged>, Task>>(); return subscription; });

        var hub = new DownloadQueueHub(bus, Substitute.For<ILogger<DownloadQueueHub>>());
        await hub.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100 && (progressHandler is null || stateHandler is null); i++)
            await Task.Delay(10, CancellationToken.None);
        progressHandler.ShouldNotBeNull();
        stateHandler.ShouldNotBeNull();

        Task DispatchProgress(DownloadProgress p)
        {
            var ctx = Substitute.For<IMessageContext<DownloadProgress>>();
            ctx.Message.Returns(p);
            return progressHandler!(ctx);
        }

        Task DispatchState(DownloadQueueStateChanged s)
        {
            var ctx = Substitute.For<IMessageContext<DownloadQueueStateChanged>>();
            ctx.Message.Returns(s);
            return stateHandler!(ctx);
        }

        return new HubHarness(hub, DispatchProgress, DispatchState);
    }

    private static async Task<DownloadProgress> ReadProgress(ChannelReader<QueueStreamEvent> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var evt = await reader.ReadAsync(cts.Token);
        return ((QueueStreamEvent.Progress)evt).Value;
    }

    private static async Task<DownloadQueueStateChanged> ReadState(ChannelReader<QueueStreamEvent> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var evt = await reader.ReadAsync(cts.Token);
        return ((QueueStreamEvent.State)evt).Value;
    }

    private static async Task<QueueStreamEvent?> TryRead(ChannelReader<QueueStreamEvent> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            return await reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private static DownloadProgress Progress(Guid jobId, int sequence, string phase = "Downloading", double? percent = 50) => new()
    {
        JobId = jobId,
        CorrelationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        OperationKey = $"job/{jobId:N}/progress/{sequence}",
        OccurredAt = Instant.FromUtc(2026, 6, 1, 0, 0),
        Attempt = 1,
        Sequence = sequence,
        SourceUrl = "https://example.test/video",
        Phase = phase,
        Percent = percent
    };

    private static DownloadQueueStateChanged StateChange(Guid jobId, DownloadJobState previous, DownloadJobState next) => new()
    {
        JobId = jobId,
        PreviousState = previous,
        State = next,
        CorrelationId = Guid.NewGuid(),
        OccurredAt = Instant.FromUtc(2026, 6, 1, 0, 0)
    };
}
