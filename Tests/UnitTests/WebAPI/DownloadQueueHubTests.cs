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
    public async Task Job_Progress_Sse_Serializes_Expected_Event_Shape()
    {
        var (hub, dispatch) = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var body = new MemoryStream();
            var controller = new DownloadQueueController(
                Substitute.For<IMessageBus>(), hub, Substitute.For<ILogger<DownloadQueueController>>());
            var httpContext = new DefaultHttpContext();
            httpContext.Response.Body = body;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // The controller registers its subscription synchronously before its first await.
            var streaming = controller.StreamJobProgressAsync(jobId, cts.Token);

            await dispatch(Progress(jobId, sequence: 3));

            for (var i = 0; i < 200 && body.Length == 0; i++)
                await Task.Delay(10, CancellationToken.None);

            await cts.CancelAsync();
            try { await streaming; } catch (OperationCanceledException) { }

            var text = Encoding.UTF8.GetString(body.ToArray());
            text.ShouldStartWith("data: ");
            text.ShouldContain($"\"jobId\":\"{jobId}\"");
            text.ShouldContain("\"sequence\":3");
            text.ShouldContain("\"phase\":\"Downloading\"");
            text.ShouldContain("\"percent\":50");
            text.ShouldEndWith("\n\n");
        }
        finally
        {
            await hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Queue_Subscriber_Receives_All_Jobs_And_Job_Subscriber_Is_Filtered()
    {
        var (hub, dispatch) = await StartHubAsync();
        try
        {
            var jobA = Guid.NewGuid();
            var jobB = Guid.NewGuid();

            var (_, queueReader) = hub.SubscribeToQueue();
            var (_, jobReader) = hub.SubscribeToJob(jobA);

            await dispatch(Progress(jobA, sequence: 1));
            await dispatch(Progress(jobB, sequence: 2));

            // Queue-wide sees both jobs.
            (await ReadAsync(queueReader)).JobId.ShouldBe(jobA);
            (await ReadAsync(queueReader)).JobId.ShouldBe(jobB);

            // Per-job sees only its own job.
            var forJob = await ReadAsync(jobReader);
            forJob.JobId.ShouldBe(jobA);
            forJob.Sequence.ShouldBe(1);
            (await TryReadAsync(jobReader)).ShouldBeNull();
        }
        finally
        {
            await hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Multiple_Subscribers_For_Same_Job_Each_Receive_The_Event()
    {
        var (hub, dispatch) = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var (_, readerA) = hub.SubscribeToJob(jobId);
            var (_, readerB) = hub.SubscribeToJob(jobId);

            await dispatch(Progress(jobId, sequence: 7));

            (await ReadAsync(readerA)).Sequence.ShouldBe(7);
            (await ReadAsync(readerB)).Sequence.ShouldBe(7);
        }
        finally
        {
            await hub.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Unsubscribed_Reader_Stops_Receiving()
    {
        var (hub, dispatch) = await StartHubAsync();
        try
        {
            var jobId = Guid.NewGuid();
            var (id, reader) = hub.SubscribeToJob(jobId);

            hub.Unsubscribe(id);
            await dispatch(Progress(jobId, sequence: 1));

            // The channel is completed, so no items are ever produced.
            (await TryReadAsync(reader)).ShouldBeNull();
        }
        finally
        {
            await hub.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<(DownloadQueueHub Hub, Func<DownloadProgress, Task> Dispatch)> StartHubAsync()
    {
        var bus = Substitute.For<IMessageBus>();
        var subscription = Substitute.For<ISubscription>();
        Func<IMessageContext<DownloadProgress>, Task>? handler = null;
        bus.SubscribeAsync(
                Arg.Any<string>(),
                Arg.Any<Func<IMessageContext<DownloadProgress>, Task>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                handler = callInfo.Arg<Func<IMessageContext<DownloadProgress>, Task>>();
                return subscription;
            });

        var hub = new DownloadQueueHub(bus, Substitute.For<ILogger<DownloadQueueHub>>());
        await hub.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100 && handler is null; i++)
            await Task.Delay(10, CancellationToken.None);
        handler.ShouldNotBeNull();

        Task Dispatch(DownloadProgress progress)
        {
            var context = Substitute.For<IMessageContext<DownloadProgress>>();
            context.Message.Returns(progress);
            return handler!(context);
        }

        return (hub, Dispatch);
    }

    private static async Task<DownloadProgress> ReadAsync(ChannelReader<DownloadProgress> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await reader.ReadAsync(cts.Token);
    }

    private static async Task<DownloadProgress?> TryReadAsync(ChannelReader<DownloadProgress> reader)
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

    private static DownloadProgress Progress(Guid jobId, int sequence) => new()
    {
        JobId = jobId,
        CorrelationId = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        OperationKey = $"job/{jobId:N}/progress/{sequence}",
        OccurredAt = Instant.FromUtc(2026, 6, 1, 0, 0),
        Attempt = 1,
        Sequence = sequence,
        SourceUrl = "https://example.test/video",
        Phase = "Downloading",
        Percent = 50
    };
}
