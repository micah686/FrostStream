using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Downloads.Controllers;
using WebAPI.Features.Downloads.Models;
using YtDlpSharpLib.Options;

namespace UnitTests.WebAPI;

public sealed class DownloadsControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 12, 0);

    [Test]
    public async Task Download_Publishes_Video_Request_With_Default_Storage()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        var result = await controller.Download(new DownloadRequest
        {
            SourceUrl = "https://example.test/video",
            StorageKey = " ",
            ForceDownload = true,
            RequestedBy = "micah",
            Tags = ["archive", "manual"],
            CookieKey = "member-cookie"
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<DownloadRequestResponse>();
        payload.JobId.ShouldNotBe(Guid.Empty);
        payload.CorrelationId.ShouldNotBe(Guid.Empty);

        await publisher.Received(1).PublishAsync(
            DownloadSubjects.DownloadRequested,
            Arg.Is<DownloadRequested>(x =>
                x.JobId == payload.JobId &&
                x.CorrelationId == payload.CorrelationId &&
                x.CausationId == null &&
                x.OperationKey == $"job/{payload.JobId:N}/requested" &&
                x.OccurredAt == Now &&
                x.Attempt == 1 &&
                x.SourceUrl == "https://example.test/video" &&
                x.StorageKey == "default" &&
                x.ForceDownload &&
                x.RequestedBy == "micah" &&
                x.Tags != null &&
                x.Tags.SequenceEqual(new[] { "archive", "manual" }) &&
                x.MediaKind == MediaKind.Video &&
                x.AudioFormat == null &&
                x.PresetKey == null &&
                x.CookieKey == "member-cookie"),
            Arg.Is<string>(x => x.Length == 32),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DownloadAudio_Publishes_Audio_Request()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        await controller.DownloadAudio(new DownloadAudioRequest
        {
            SourceUrl = "https://example.test/audio",
            StorageKey = "storage-a"
        }, CancellationToken.None);

        await publisher.Received(1).PublishAsync(
            DownloadSubjects.DownloadRequested,
            Arg.Is<DownloadRequested>(x =>
                x.SourceUrl == "https://example.test/audio" &&
                x.StorageKey == "storage-a" &&
                x.MediaKind == MediaKind.Audio &&
                x.AudioFormat == AudioConversionFormat.Mp3),
            Arg.Any<string>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DownloadWithPreset_Publishes_Preset_Key()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        await controller.DownloadWithPreset(new DownloadPresetRequest
        {
            SourceUrl = "https://example.test/video",
            StorageKey = "storage-a",
            PresetKey = "audio-high"
        }, CancellationToken.None);

        await publisher.Received(1).PublishAsync(
            DownloadSubjects.DownloadRequested,
            Arg.Is<DownloadRequested>(x =>
                x.MediaKind == MediaKind.Video &&
                x.AudioFormat == null &&
                x.PresetKey == "audio-high" &&
                x.YtDlpOptions == null),
            Arg.Any<string>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Download_Returns_502_When_Publish_Fails()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        publisher.PublishAsync(
                Arg.Any<string>(),
                Arg.Any<DownloadRequested>(),
                Arg.Any<string>(),
                null,
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("nats unavailable"));
        var controller = CreateController(publisher);

        var result = await controller.Download(new DownloadRequest
        {
            SourceUrl = "https://example.test/video",
            StorageKey = "storage-a"
        }, CancellationToken.None);

        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
        objectResult.Value.ShouldBeOfType<ProblemDetails>().Title.ShouldBe("Failed to submit download request");
    }

    private static DownloadsController CreateController(IJetStreamPublisher publisher)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        return new DownloadsController(
            publisher,
            clock,
            Substitute.For<ILogger<DownloadsController>>());
    }
}
