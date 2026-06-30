using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Auth;
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
            Tags = ["archive", "manual"],
            CookieProfileKey = "member-cookie"
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
                x.RequestedBy == "unit_test_user" &&
                x.Tags != null &&
                x.Tags.SequenceEqual(new[] { "archive", "manual" }) &&
                x.MediaKind == MediaKind.Video &&
                x.AudioFormat == null &&
                x.PresetKey == null &&
                x.CookieSecretPath == "cookies/users/unit_test_user/member-cookie"),
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
    public async Task Download_Publishes_SponsorBlock_Options()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        await controller.Download(new DownloadRequest
        {
            SourceUrl = "https://example.test/video",
            StorageKey = "storage-a",
            SponsorBlock = new SponsorBlockRequest
            {
                MarkCategories = " all,-preview ",
                RemoveCategories = "sponsor,selfpromo",
                ChapterTitleTemplate = "[SponsorBlock]: %(category_names)l",
                ApiUrl = "https://sponsor.example.test"
            }
        }, CancellationToken.None);

        await publisher.Received(1).PublishAsync(
            DownloadSubjects.DownloadRequested,
            Arg.Is<DownloadRequested>(x =>
                x.SourceUrl == "https://example.test/video" &&
                x.YtDlpOptions != null &&
                x.YtDlpOptions.SponsorBlock.SponsorblockMark == "all,-preview" &&
                x.YtDlpOptions.SponsorBlock.SponsorblockRemove == "sponsor,selfpromo" &&
                x.YtDlpOptions.SponsorBlock.SponsorblockChapterTitle == "[SponsorBlock]: %(category_names)l" &&
                x.YtDlpOptions.SponsorBlock.SponsorblockApi == "https://sponsor.example.test" &&
                !x.YtDlpOptions.SponsorBlock.NoSponsorblock),
            Arg.Any<string>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DownloadAudio_Can_Disable_SponsorBlock()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        await controller.DownloadAudio(new DownloadAudioRequest
        {
            SourceUrl = "https://example.test/audio",
            StorageKey = "storage-a",
            SponsorBlock = new SponsorBlockRequest { Disable = true }
        }, CancellationToken.None);

        await publisher.Received(1).PublishAsync(
            DownloadSubjects.DownloadRequested,
            Arg.Is<DownloadRequested>(x =>
                x.MediaKind == MediaKind.Audio &&
                x.AudioFormat == AudioConversionFormat.Mp3 &&
                x.YtDlpOptions != null &&
                x.YtDlpOptions.SponsorBlock.NoSponsorblock),
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

    [Test]
    public async Task Download_Rejects_Non_Http_Source_Url()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        var result = await controller.Download(new DownloadRequest
        {
            SourceUrl = "file:///etc/passwd",
            StorageKey = "storage-a"
        }, CancellationToken.None);

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value.ShouldBeOfType<ProblemDetails>().Title.ShouldBe("Invalid source URL");
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default!,
            default(DownloadRequested)!,
            default,
            default,
            default);
    }

    [Test]
    public async Task Download_Rejects_Private_Ip_Source_Url()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        var result = await controller.Download(new DownloadRequest
        {
            SourceUrl = "http://169.254.169.254/latest/meta-data",
            StorageKey = "storage-a"
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default!,
            default(DownloadRequested)!,
            default,
            default,
            default);
    }

    [Test]
    public async Task Cancel_Requests_Download_Cancellation()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var messageBus = Substitute.For<IMessageBus>();
        var jobId = Guid.NewGuid();
        messageBus.RequestAsync<CancelDownloadRequest, CancelDownloadResponse>(
                DownloadSubjects.CancelDownloadRequest,
                Arg.Any<CancelDownloadRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CancelDownloadResponse
            {
                Success = true,
                State = DownloadJobState.Cancelling
            });
        var controller = CreateController(publisher, messageBus);

        var result = await controller.Cancel(
            jobId,
            new CancelDownloadApiRequest { Reason = "clicked stop" },
            CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        accepted.Value.ShouldBeOfType<CancelDownloadApiResponse>().State.ShouldBe(DownloadJobState.Cancelling);
        await messageBus.Received(1).RequestAsync<CancelDownloadRequest, CancelDownloadResponse>(
            DownloadSubjects.CancelDownloadRequest,
            Arg.Is<CancelDownloadRequest>(x =>
                x.JobId == jobId &&
                x.RequestedBy == "unit_test_user" &&
                x.Reason == "clicked stop"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    private static DownloadsController CreateController(IJetStreamPublisher publisher, IMessageBus? messageBus = null)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        var controller = new DownloadsController(
            publisher,
            messageBus ?? Substitute.For<IMessageBus>(),
            clock,
            Substitute.For<ILogger<DownloadsController>>());

        // The controller stamps RequestedBy from the validated token subject, so give it one.
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, "unit_test_user")], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }
}
