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
using WebAPI.Features.Statistics.Controllers;
using WebAPI.Features.Statistics.Models;

namespace UnitTests.WebAPI;

public sealed class StatisticsControllerTests
{
    [Test]
    public async Task Overview_Forwards_Current_User_Subject()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<StatisticsOverviewRequestMessage, StatisticsOverviewResponseMessage>(
                StatisticsSubjects.Overview,
                Arg.Is<StatisticsOverviewRequestMessage>(x => x.OwnerSubject == "reader-1"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StatisticsOverviewResponseMessage
            {
                Success = true,
                Overview = Overview()
            });

        var result = await controller.GetOverview(CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<StatisticsOverviewDto>();
        payload.Inventory.TotalMedia.ShouldBe(3);
    }

    [Test]
    public async Task ListChannels_Returns_Public_List_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<StatisticsChannelsListRequestMessage, StatisticsChannelsListResponseMessage>(
                StatisticsSubjects.ChannelsList,
                Arg.Is<StatisticsChannelsListRequestMessage>(x =>
                    x.PageSize == 10 &&
                    x.Page == 2 &&
                    x.SortBy == "bytes" &&
                    x.SortOrder == "asc"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StatisticsChannelsListResponseMessage
            {
                Success = true,
                Page = 2,
                TotalCount = 21,
                HasMore = true,
                Items =
                [
                    new ChannelStatisticsSummaryDto
                    {
                        CreatorSourceId = 7,
                        Platform = "youtube",
                        SourceType = "Videos",
                        SourceUrl = "https://example.test/channel",
                        AvailableCount = 5,
                        DownloadedCount = 2
                    }
                ]
            });

        var result = await controller.ListChannels(10, 2, "bytes", "asc", CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<ChannelStatisticsListResponse>();
        payload.Page.ShouldBe(2);
        payload.TotalCount.ShouldBe(21);
        payload.Items.Single().CreatorSourceId.ShouldBe(7);
    }

    [Test]
    public async Task GetChannel_Maps_NotFound()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<StatisticsChannelGetRequestMessage, StatisticsChannelGetResponseMessage>(
                StatisticsSubjects.ChannelGet,
                Arg.Is<StatisticsChannelGetRequestMessage>(x => x.CreatorSourceId == 404),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StatisticsChannelGetResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.GetChannel(404, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task DownloadHistory_Rejects_Invalid_Bucket()
    {
        var controller = CreateController(Substitute.For<IMessageBus>());

        var result = await controller.GetDownloadHistory(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-02-01T00:00:00Z"),
            "hour",
            CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    private static StatisticsController CreateController(IMessageBus bus)
    {
        var controller = new StatisticsController(
            bus,
            Substitute.For<ILogger<StatisticsController>>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(AuthConstants.SubjectClaim, "reader-1")],
                    "test"))
            }
        };
        return controller;
    }

    private static StatisticsOverviewDto Overview()
        => new()
        {
            Inventory = new InventoryStatisticsDto { TotalMedia = 3 },
            WatchProgress = new WatchStatisticsDto(),
            MediaTypes = [],
            DownloadStates = []
        };
}
