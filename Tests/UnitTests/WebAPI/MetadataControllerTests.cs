using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Controllers;

namespace UnitTests.WebAPI;

public sealed class MetadataControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 17, 0);

    [Test]
    public async Task List_Sends_Filter_Request_And_Returns_Paged_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        var item = CreateCard(Guid.NewGuid());

        bus.RequestAsync<MetadataListRequestMessage, MetadataListResponseMessage>(
                MetadataSubjects.List,
                Arg.Is<MetadataListRequestMessage>(x =>
                    x.PageSize == 12 &&
                    x.Page == 3 &&
                    x.SortBy == "title" &&
                    x.SortOrder == "asc" &&
                    x.Platform == "youtube" &&
                    x.AccountId == 10 &&
                    x.Tag == "music" &&
                    x.Category == "education" &&
                    x.Genre == "documentary" &&
                    x.CaptionLanguage == "en"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataListResponseMessage
            {
                Success = true,
                Items = [item],
                Page = 3,
                TotalCount = 30,
                HasMore = true
            });

        var result = await controller.List(
            pageSize: 12,
            page: 3,
            sortBy: "title",
            sortOrder: "asc",
            platform: "youtube",
            accountId: 10,
            tag: "music",
            category: "education",
            genre: "documentary",
            captionLanguage: "en",
            cancellationToken: CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<PagedMetadataResponse<MetadataCardDto>>();
        payload.Items.Single().MediaGuid.ShouldBe(item.MediaGuid);
        payload.Page.ShouldBe(3);
        payload.TotalCount.ShouldBe(30);
        payload.HasMore.ShouldBeTrue();
    }

    [Test]
    public async Task Search_Rejects_Blank_Query()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);

        var result = await controller.Search(" ", cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>().Value.ShouldBe("Query parameter 'q' is required.");
        await bus.DidNotReceive().RequestAsync<MetadataSearchRequestMessage, MetadataSearchResponseMessage>(
            Arg.Any<string>(),
            Arg.Any<MetadataSearchRequestMessage>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Search_Sends_Query_And_Returns_Results()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        var item = CreateCard(Guid.NewGuid());

        bus.RequestAsync<MetadataSearchRequestMessage, MetadataSearchResponseMessage>(
                MetadataSubjects.Search,
                Arg.Is<MetadataSearchRequestMessage>(x =>
                    x.Query == "needle" &&
                    x.PageSize == 5 &&
                    x.Page == 2 &&
                    x.Platform == "youtube" &&
                    x.Tag == "tag" &&
                    x.Category == "cat" &&
                    x.Genre == "genre" &&
                    x.SortBy == "release_date" &&
                    x.SortOrder == "desc"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataSearchResponseMessage
            {
                Success = true,
                Items = [item],
                Page = 2,
                TotalCount = 1,
                HasMore = false
            });

        var result = await controller.Search(
            q: "needle",
            pageSize: 5,
            page: 2,
            platform: "youtube",
            tag: "tag",
            category: "cat",
            genre: "genre",
            sortBy: "release_date",
            sortOrder: "desc",
            cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<PagedMetadataResponse<MetadataCardDto>>()
            .Items.Single().Title.ShouldBe("Video");
    }

    [Test]
    public async Task Get_Maps_NotFound_And_Invalid_Service_Response()
    {
        var mediaGuid = Guid.NewGuid();

        var notFound = await GetWith(mediaGuid, new MetadataGetResponseMessage
        {
            Success = false,
            ErrorCode = "not_found",
            ErrorMessage = "missing"
        });
        notFound.Result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");

        var invalid = await GetWith(mediaGuid, new MetadataGetResponseMessage { Success = true, Item = null });
        invalid.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task GetTechnical_Returns_Item()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        var mediaGuid = Guid.NewGuid();

        bus.RequestAsync<MetadataTechnicalRequestMessage, MetadataTechnicalResponseMessage>(
                MetadataSubjects.GetTechnical,
                Arg.Is<MetadataTechnicalRequestMessage>(x => x.MediaGuid == mediaGuid),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataTechnicalResponseMessage
            {
                Success = true,
                Item = new MetadataTechnicalDto { MediaGuid = mediaGuid, DurationTicks = 100 }
            });

        var result = await controller.GetTechnical(mediaGuid, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<MetadataTechnicalDto>().DurationTicks.ShouldBe(100);
    }

    [Test]
    public async Task ListComments_Sends_Query_And_Returns_Paged_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        var mediaGuid = Guid.NewGuid();
        var comment = new CommentDto
        {
            CommentId = "comment-1",
            Text = "hello",
            CommentTimestamp = Now,
            Account = CreateAccountCard()
        };

        bus.RequestAsync<MetadataCommentsListRequestMessage, MetadataCommentsListResponseMessage>(
                MetadataSubjects.CommentsList,
                Arg.Is<MetadataCommentsListRequestMessage>(x =>
                    x.MediaGuid == mediaGuid &&
                    x.PageSize == 10 &&
                    x.Page == 4 &&
                    x.Query == "hello" &&
                    x.ParentCommentId == "parent" &&
                    x.SortBy == "like_count" &&
                    x.SortOrder == "asc"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataCommentsListResponseMessage
            {
                Success = true,
                Items = [comment],
                Page = 4,
                TotalCount = 9,
                HasMore = true
            });

        var result = await controller.ListComments(
            mediaGuid,
            pageSize: 10,
            page: 4,
            q: "hello",
            parentId: "parent",
            sortBy: "like_count",
            sortOrder: "asc",
            cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<PagedMetadataResponse<CommentDto>>()
            .Items.Single().CommentId.ShouldBe("comment-1");
    }

    [Test]
    public async Task Captions_Accounts_And_Taxonomy_Map_Success_Responses()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        var mediaGuid = Guid.NewGuid();

        bus.RequestAsync<MetadataCaptionsListRequestMessage, MetadataCaptionsListResponseMessage>(
                MetadataSubjects.CaptionsList,
                Arg.Is<MetadataCaptionsListRequestMessage>(x =>
                    x.MediaGuid == mediaGuid &&
                    x.LanguageCode == "en" &&
                    x.CaptionType == "manual"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataCaptionsListResponseMessage
            {
                Success = true,
                Items = [new CaptionDto { LanguageCode = "en", CaptionType = "manual", StoragePath = "captions/en.vtt" }],
                TotalCount = 1
            });
        bus.RequestAsync<MetadataAccountsListRequestMessage, MetadataAccountsListResponseMessage>(
                MetadataSubjects.AccountsList,
                Arg.Is<MetadataAccountsListRequestMessage>(x =>
                    x.PageSize == 7 &&
                    x.After == "cursor" &&
                    x.Platform == "youtube"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataAccountsListResponseMessage
            {
                Success = true,
                Items = [new AccountSummaryDto { AccountId = 1, Platform = "youtube", AccountName = "Creator", AccountHandle = "@creator" }],
                NextCursor = "next",
                HasMore = true
            });
        bus.RequestAsync<MetadataTaxonomyListRequestMessage, MetadataTaxonomyListResponseMessage>(
                MetadataSubjects.TaxonomyTagsList,
                Arg.Is<MetadataTaxonomyListRequestMessage>(x =>
                    x.PageSize == 9 &&
                    x.PageOffset == 18 &&
                    x.Search == "tag"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataTaxonomyListResponseMessage
            {
                Success = true,
                Items = [new TaxonomyItemDto { Name = "tag", MediaCount = 3 }],
                Total = 1
            });

        var captions = await controller.ListCaptions(mediaGuid, "en", "manual", CancellationToken.None);
        captions.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<MetadataListResponse<CaptionDto>>().TotalCount.ShouldBe(1);

        var accounts = await controller.ListAccounts(7, "cursor", "youtube", CancellationToken.None);
        accounts.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<AccountListResponse>().NextCursor.ShouldBe("next");

        var tags = await controller.ListTags(9, 18, "tag", CancellationToken.None);
        tags.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<TaxonomyListResponse>().Total.ShouldBe(1);
    }

    [Test]
    public async Task List_Returns_503_When_Bus_Throws()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);

        bus.RequestAsync<MetadataListRequestMessage, MetadataListResponseMessage>(
                Arg.Any<string>(),
                Arg.Any<MetadataListRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MetadataListResponseMessage?>(
                new InvalidOperationException("nats unavailable")));

        var result = await controller.List(cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task Account_And_AccountMedia_Send_Expected_Requests()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);

        bus.RequestAsync<MetadataAccountGetRequestMessage, MetadataAccountGetResponseMessage>(
                MetadataSubjects.AccountsGet,
                Arg.Is<MetadataAccountGetRequestMessage>(x => x.AccountId == 22),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataAccountGetResponseMessage
            {
                Success = true,
                Item = new AccountDto { AccountId = 22, Platform = "youtube", AccountName = "Creator", AccountHandle = "@creator" }
            });
        bus.RequestAsync<MetadataListRequestMessage, MetadataListResponseMessage>(
                MetadataSubjects.AccountsMediaList,
                Arg.Is<MetadataListRequestMessage>(x =>
                    x.AccountId == 22 &&
                    x.PageSize == 6 &&
                    x.Page == 2 &&
                    x.SortBy == "title" &&
                    x.SortOrder == "asc"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MetadataListResponseMessage
            {
                Success = true,
                Items = [CreateCard(Guid.NewGuid())],
                Page = 2,
                TotalCount = 1
            });

        var account = await controller.GetAccount(22, CancellationToken.None);
        account.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<AccountDto>().AccountId.ShouldBe(22);

        var media = await controller.ListAccountMedia(22, 6, 2, "title", "asc", CancellationToken.None);
        media.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<PagedMetadataResponse<MetadataCardDto>>()
            .Items.Single().Title.ShouldBe("Video");
    }

    private static async Task<ActionResult<MetadataDetailDto>> GetWith(
        Guid mediaGuid,
        MetadataGetResponseMessage response)
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = CreateController(bus);
        bus.RequestAsync<MetadataGetRequestMessage, MetadataGetResponseMessage>(
                MetadataSubjects.Get,
                Arg.Is<MetadataGetRequestMessage>(x => x.MediaGuid == mediaGuid),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(response);

        return await controller.Get(mediaGuid, CancellationToken.None);
    }

    private static MetadataController CreateController(IMessageBus bus)
        => new(bus, Substitute.For<ILogger<MetadataController>>());

    private static MetadataCardDto CreateCard(Guid mediaGuid) => new()
    {
        MediaGuid = mediaGuid,
        Title = "Video",
        ReleaseDate = Now,
        Account = CreateAccountCard()
    };

    private static MetadataAccountCardDto CreateAccountCard() => new()
    {
        AccountId = 1,
        Platform = "youtube",
        AccountName = "Creator",
        AccountHandle = "@creator"
    };
}
