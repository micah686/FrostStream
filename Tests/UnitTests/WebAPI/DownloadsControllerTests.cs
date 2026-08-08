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

        var payload = result.Result!.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<DownloadRequestResponse>();
        payload.JobId.ShouldNotBe(Guid.Empty);
        payload.CorrelationId.ShouldNotBe(Guid.Empty);

        await publisher.Received(1).PublishAsync(
            DownloadSubjects.GroupRequested,
            Arg.Is<DownloadGroupRequested>(g => g != null &&
                g.Kind == DownloadGroupKind.Direct &&
                g.GroupId == payload.CorrelationId &&
                g.CorrelationId == payload.CorrelationId &&
                g.DirectRequest != null &&
                g.DirectRequest.JobId == payload.JobId &&
                g.DirectRequest.CorrelationId == payload.CorrelationId &&
                g.DirectRequest.CausationId == null &&
                g.DirectRequest.OperationKey == $"job/{payload.JobId:N}/requested" &&
                g.DirectRequest.OccurredAt == Now &&
                g.DirectRequest.Attempt == 1 &&
                g.DirectRequest.SourceUrl == "https://example.test/video" &&
                g.DirectRequest.StorageKey == "default" &&
                g.DirectRequest.ForceDownload &&
                g.DirectRequest.RequestedBy == "unit_test_user" &&
                g.DirectRequest.Tags != null &&
                g.DirectRequest.Tags.SequenceEqual(new[] { "archive", "manual" }) &&
                g.DirectRequest.MediaKind == MediaKind.Video &&
                g.DirectRequest.AudioFormat == null &&
                g.DirectRequest.PresetKey == null &&
                g.DirectRequest.CookieSecretPath == "cookies/users/unit_test_user/member-cookie"),
            Arg.Is<string>(x => x != null && x.Length == 32),
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
            DownloadSubjects.GroupRequested,
            Arg.Is<DownloadGroupRequested>(g => g != null &&
                g.DirectRequest != null &&
                g.DirectRequest.SourceUrl == "https://example.test/video" &&
                g.DirectRequest.YtDlpOptions != null &&
                g.DirectRequest.YtDlpOptions.SponsorBlock.SponsorblockMark == "all,-preview" &&
                g.DirectRequest.YtDlpOptions.SponsorBlock.SponsorblockRemove == "sponsor,selfpromo" &&
                g.DirectRequest.YtDlpOptions.SponsorBlock.SponsorblockChapterTitle == "[SponsorBlock]: %(category_names)l" &&
                g.DirectRequest.YtDlpOptions.SponsorBlock.SponsorblockApi == "https://sponsor.example.test" &&
                !g.DirectRequest.YtDlpOptions.SponsorBlock.NoSponsorblock),
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
                Arg.Any<DownloadGroupRequested>(),
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

        var objectResult = result.Result!.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
        objectResult.Value!.ShouldBeOfType<ProblemDetails>().Title.ShouldBe("Failed to submit download request");
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

        var badRequest = result.Result!.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.ShouldBeOfType<ProblemDetails>().Title.ShouldBe("Invalid source URL");
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default!,
            default(DownloadGroupRequested)!,
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

        result.Result!.ShouldBeOfType<BadRequestObjectResult>();
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(
            default!,
            default(DownloadGroupRequested)!,
            default,
            default,
            default);
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
