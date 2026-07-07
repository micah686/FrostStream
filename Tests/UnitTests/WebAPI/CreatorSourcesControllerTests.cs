using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Auth;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.CreatorSources.Controllers;
using WebAPI.Features.CreatorSources.Models;

namespace UnitTests.WebAPI;

public sealed class CreatorSourcesControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 16, 0);

    [Test]
    public async Task Create_Sends_Request_And_Returns_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);

        bus.RequestAsync<CreatorSourceCreateRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.CreateSource,
                Arg.Is<CreatorSourceCreateRequestMessage>(x =>
                    x.Platform == "youtube" &&
                    x.SourceType == CreatorSourceType.Videos &&
                    x.SourceUrl == "https://example.test/@creator" &&
                    x.ScanEnabled &&
                    x.IncrementalPageSize == 75 &&
                    x.ConsecutiveKnownThreshold == 15 &&
                    x.FullRescanIntervalDays == 14 &&
                    x.MetadataRefreshWindow == 50 &&
                    x.ProviderQueryLimits != null &&
                    x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Videos) == 125 &&
                    x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Streams) == 25 &&
                    x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Shorts) == 75),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Entity = CreateDto(42)
            });

        var result = await controller.Create(new CreatorSourceCreateRequest
        {
            Platform = "youtube",
            SourceType = CreatorSourceType.Videos,
            SourceUrl = "https://example.test/@creator",
            ScanEnabled = true,
            IncrementalPageSize = 75,
            ConsecutiveKnownThreshold = 15,
            FullRescanIntervalDays = 14,
            MetadataRefreshWindow = 50,
            ProviderQueryLimits = YouTubeLimits(videos: 125, streams: 25, shorts: 75)
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CreatorSourceResponse>();
        payload.Id.ShouldBe(42);
        payload.Platform.ShouldBe("youtube");
        payload.ProviderQueryLimits.ShouldNotBeNull();
    }

    [Test]
    public async Task List_Returns_Items()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);

        bus.RequestAsync<CreatorSourceListRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.ListSources,
                Arg.Any<CreatorSourceListRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Items = [CreateDto(10), CreateDto(11)]
            });

        var result = await controller.List(CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeAssignableTo<IReadOnlyCollection<CreatorSourceResponse>>();
        payload.ShouldNotBeNull();
        payload.Count.ShouldBe(2);
    }

    [Test]
    public async Task Get_Maps_NotFound_To_404()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);

        bus.RequestAsync<CreatorSourceGetRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.GetSource,
                Arg.Is<CreatorSourceGetRequestMessage>(x => x.Id == 99),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Get(99, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    [Test]
    public async Task RefreshAssets_Gets_Source_And_Publishes_Channel_Asset_Refresh()
    {
        var bus = Substitute.For<IMessageBus>();
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(bus, publisher);

        bus.RequestAsync<CreatorSourceGetRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.GetSource,
                Arg.Is<CreatorSourceGetRequestMessage>(x => x.Id == 42),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Entity = CreateDto(42)
            });

        var result = await controller.RefreshAssets(42, force: true, CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        accepted.Value.ShouldNotBeNull();
        await publisher.Received(1).PublishAsync(
            BackgroundJobSubjects.ChannelAssetRefreshRequest,
            Arg.Is<ChannelAssetRefreshRequested>(x =>
                x.ScheduleKey == "manual" &&
                x.TaskType == "channel_asset_refresh" &&
                x.DueWindowUtc == Now &&
                x.OccurredAt == Now &&
                x.TargetSourceId == 42 &&
                x.Force),
            Arg.Is<string>(x => x.StartsWith("manual:42:", StringComparison.Ordinal)),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DownloadChannel_Creates_Or_Reuses_Source_And_Publishes_Targeted_Channel_Scan()
    {
        var bus = Substitute.For<IMessageBus>();
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(bus, publisher);

        bus.RequestAsync<CreatorSourceCreateOrReuseRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.CreateOrReuseSource,
                Arg.Is<CreatorSourceCreateOrReuseRequestMessage>(x =>
                    x.Platform == "youtube" &&
                    x.SourceType == CreatorSourceType.Videos &&
                    x.SourceUrl == "https://example.test/@creator/videos" &&
                    x.ScanEnabled &&
                    x.ProviderQueryLimits != null &&
                    x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Videos) == 100),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Entity = CreateDto(42) with { SourceUrl = "https://example.test/@creator/videos" }
            });

        var result = await controller.DownloadChannel(new ChannelDownloadRequest
        {
            SourceUrl = "https://example.test/@creator/videos",
            StorageKey = "archive",
            ProviderQueryLimits = YouTubeLimits(videos: 100, streams: 20, shorts: 40)
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<ChannelDownloadResponse>();
        payload.SourceId.ShouldBe(42);
        payload.Queued.ShouldBeTrue();
        payload.IdempotencyKey.ShouldStartWith("manual-channel-download:42:");

        await publisher.Received(1).PublishAsync(
            BackgroundJobSubjects.ChannelMediaListRequest,
            Arg.Is<ChannelMediaListRequested>(x =>
                x.ScheduleKey == "manual-channel-download" &&
                x.TaskType == "channel_media_list" &&
                x.DueWindowUtc == Now &&
                x.OccurredAt == Now &&
                x.TargetSourceId == 42 &&
                x.StorageKey == "archive" &&
                x.RequestedBy == "unit_test_user" &&
                x.ProviderQueryLimits != null &&
                x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Videos) == 100 &&
                x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Streams) == 20 &&
                x.ProviderQueryLimits.GetLimit("youtube", CreatorSourceType.Shorts) == 40),
            Arg.Is<string>(x => x.StartsWith("manual-channel-download:42:", StringComparison.Ordinal)),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefreshAssets_Returns_503_When_Publish_Fails()
    {
        var bus = Substitute.For<IMessageBus>();
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(bus, publisher);

        bus.RequestAsync<CreatorSourceGetRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.GetSource,
                Arg.Any<CreatorSourceGetRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = true,
                Entity = CreateDto(42)
            });
        publisher.PublishAsync(
                Arg.Any<string>(),
                Arg.Any<ChannelAssetRefreshRequested>(),
                Arg.Any<string>(),
                null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("nats unavailable")));

        var result = await controller.RefreshAssets(42, force: false, CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task Delete_Maps_Validation_To_400()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);

        bus.RequestAsync<CreatorSourceDeleteRequestMessage, CreatorSourceOperationResponseMessage>(
                CreatorDiscoverySubjects.DeleteSource,
                Arg.Is<CreatorSourceDeleteRequestMessage>(x => x.Id == 42),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CreatorSourceOperationResponseMessage
            {
                Success = false,
                ErrorCode = "validation",
                ErrorMessage = "invalid"
            });

        var result = await controller.Delete(42, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe("invalid");
    }

    [Test]
    public async Task Create_Rejects_Link_Local_Source_Url()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus: bus);

        var result = await controller.Create(new CreatorSourceCreateRequest
        {
            Platform = "youtube",
            SourceType = CreatorSourceType.Videos,
            SourceUrl = "http://[fe80::1]/@creator"
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await bus.DidNotReceiveWithAnyArgs()
            .RequestAsync<CreatorSourceCreateRequestMessage, CreatorSourceOperationResponseMessage>(
                default!,
                default!,
                default,
                default);
    }

    private static CreatorSourcesController CreateController(
        IMessageBus? bus = null,
        IJetStreamPublisher? publisher = null)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        var controller = new CreatorSourcesController(
            bus ?? Substitute.For<IMessageBus>(),
            publisher ?? Substitute.For<IJetStreamPublisher>(),
            clock,
            Substitute.For<ILogger<CreatorSourcesController>>());

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, "unit_test_user")], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }

    private static CreatorSourceDto CreateDto(long id) => new()
    {
        Id = id,
        Platform = "youtube",
        SourceType = CreatorSourceType.Videos,
        SourceUrl = "https://example.test/@creator",
        ScanEnabled = true,
        IncrementalPageSize = 75,
        ConsecutiveKnownThreshold = 15,
        FullRescanIntervalDays = 14,
        UpdateCheckIntervalHours = 6,
        MetadataRefreshWindow = 50,
        ProviderQueryLimits = YouTubeLimits(videos: 125, streams: 25, shorts: 75),
        CreatedAt = Now,
        LastUpdated = Now
    };

    private static CreatorSourceProviderQueryLimits YouTubeLimits(int? videos, int? streams, int? shorts)
        => new()
        {
            Providers =
            {
                ["youtube"] = new CreatorSourceTypeQueryLimits
                {
                    Videos = videos,
                    Streams = streams,
                    Shorts = shorts
                }
            }
        };
}
