using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Downloads;
using WebAPI.Features.Downloads.Controllers;
using WebAPI.Features.Downloads.Models;

namespace UnitTests.WebAPI;

public sealed class DownloadQueueControllerTests
{
    [Test]
    public async Task List_Maps_Successful_Response_To_200()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var job = new DownloadQueueJobDto
        {
            JobId = Guid.NewGuid(),
            SourceUrl = "https://example.test/video",
            State = DownloadJobState.Completed
        };
        messageBus.RequestAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
                DownloadQueueSubjects.List,
                Arg.Any<DownloadQueueListRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new DownloadQueueListResponse
            {
                Success = true,
                Items = [job],
                NextCursor = "next",
                TotalCount = 1
            });
        var controller = CreateController(messageBus);

        var result = await controller.List(
            state: null, sourceKind: null, requestedBy: null, storageKey: null,
            createdFrom: null, createdTo: null, q: null,
            cancellationToken: CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<DownloadQueueListResponse>();
        payload.TotalCount.ShouldBe(1);
        payload.NextCursor.ShouldBe("next");
        payload.Items.ShouldHaveSingleItem().JobId.ShouldBe(job.JobId);
    }

    [Test]
    public async Task List_Passes_Filters_And_Sort_Through()
    {
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.RequestAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
                Arg.Any<string>(), Arg.Any<DownloadQueueListRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadQueueListResponse { Success = true });
        var controller = CreateController(messageBus);

        await controller.List(
            state: DownloadJobState.DownloadQueued,
            sourceKind: DownloadSourceKind.Playlist,
            requestedBy: "alice",
            storageKey: "nas",
            createdFrom: null, createdTo: null,
            q: "cats",
            limit: 25,
            cursor: "abc",
            sort: "priority",
            cancellationToken: CancellationToken.None);

        await messageBus.Received(1).RequestAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
            DownloadQueueSubjects.List,
            Arg.Is<DownloadQueueListRequest>(x =>
                x.State == DownloadJobState.DownloadQueued &&
                x.SourceKind == DownloadSourceKind.Playlist &&
                x.RequestedBy == "alice" &&
                x.StorageKey == "nas" &&
                x.Query == "cats" &&
                x.Limit == 25 &&
                x.Cursor == "abc" &&
                x.Sort == DownloadQueueSort.Priority),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Rejects_Unknown_Sort()
    {
        var controller = CreateController(Substitute.For<IMessageBus>());

        var result = await controller.List(
            state: null, sourceKind: null, requestedBy: null, storageKey: null,
            createdFrom: null, createdTo: null, q: null,
            sort: "bogus",
            cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task List_Maps_Timeout_To_502()
    {
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.RequestAsync<DownloadQueueListRequest, DownloadQueueListResponse>(
                Arg.Any<string>(), Arg.Any<DownloadQueueListRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((DownloadQueueListResponse?)null);
        var controller = CreateController(messageBus);

        var result = await controller.List(
            state: null, sourceKind: null, requestedBy: null, storageKey: null,
            createdFrom: null, createdTo: null, q: null,
            cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task Get_Maps_Success_To_200()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var jobId = Guid.NewGuid();
        messageBus.RequestAsync<DownloadQueueGetRequest, DownloadQueueGetResponse>(
                DownloadQueueSubjects.Get,
                Arg.Is<DownloadQueueGetRequest>(x => x.JobId == jobId),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new DownloadQueueGetResponse
            {
                Success = true,
                Job = new DownloadQueueJobDto { JobId = jobId, SourceUrl = "https://example.test/video" }
            });
        var controller = CreateController(messageBus);

        var result = await controller.Get(jobId, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<DownloadQueueJobDto>().JobId.ShouldBe(jobId);
    }

    [Test]
    public async Task Get_Returns_404_For_Missing_Job()
    {
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.RequestAsync<DownloadQueueGetRequest, DownloadQueueGetResponse>(
                Arg.Any<string>(), Arg.Any<DownloadQueueGetRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadQueueGetResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "nope" });
        var controller = CreateController(messageBus);

        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task History_Returns_404_For_Missing_Job()
    {
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.RequestAsync<DownloadQueueHistoryRequest, DownloadQueueHistoryResponse>(
                Arg.Any<string>(), Arg.Any<DownloadQueueHistoryRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadQueueHistoryResponse { Success = false, ErrorCode = "not_found" });
        var controller = CreateController(messageBus);

        var result = await controller.History(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Test]
    public async Task History_Maps_Success_To_200()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var entry = new DownloadQueueHistoryEntryDto { Id = 1, OperationKey = "op", EventName = nameof(DownloadRequested) };
        messageBus.RequestAsync<DownloadQueueHistoryRequest, DownloadQueueHistoryResponse>(
                Arg.Any<string>(), Arg.Any<DownloadQueueHistoryRequest>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadQueueHistoryResponse { Success = true, Entries = [entry] });
        var controller = CreateController(messageBus);

        var result = await controller.History(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeAssignableTo<IReadOnlyList<DownloadQueueHistoryEntryDto>>()!
            .ShouldHaveSingleItem().EventName.ShouldBe(nameof(DownloadRequested));
    }

    [Test]
    public async Task Stop_Requests_V2_Stop_With_Authenticated_User()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var jobId = Guid.NewGuid();
        messageBus.RequestAsync<StopDownloadRequest, StopDownloadResponse>(
                DownloadSubjects.StopDownloadRequest,
                Arg.Any<StopDownloadRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StopDownloadResponse { Success = true, Status = DownloadJobStatus.Stopping });
        var controller = CreateController(messageBus);

        var result = await controller.Stop(jobId, new StopDownloadApiRequest { Reason = "stop" }, CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<StopDownloadApiResponse>().Status.ShouldBe(DownloadJobStatus.Stopping);
        await messageBus.Received(1).RequestAsync<StopDownloadRequest, StopDownloadResponse>(
            DownloadSubjects.StopDownloadRequest,
            Arg.Is<StopDownloadRequest>(x => x.JobId == jobId && x.RequestedBy == "unit_test_user" && x.Reason == "stop"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdatePriority_Alias_Returns_NoContent_On_Success()
    {
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.RequestAsync<UpdateDownloadPriorityRequest, UpdateDownloadPriorityResponse>(
                DownloadSubjects.UpdatePriorityRequest,
                Arg.Any<UpdateDownloadPriorityRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new UpdateDownloadPriorityResponse { Success = true });
        var controller = CreateController(messageBus);

        var result = await controller.UpdatePriority(Guid.NewGuid(), new UpdatePriorityRequest { Priority = 50 }, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Test]
    public async Task Start_Returns_Accepted_With_Fresh_Run_On_Success()
    {
        var messageBus = Substitute.For<IMessageBus>();
        var jobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        messageBus.RequestAsync<StartDownloadRequest, StartDownloadResponse>(
                DownloadSubjects.StartDownloadRequest,
                Arg.Any<StartDownloadRequest>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new StartDownloadResponse { Success = true, JobId = jobId, RunId = runId });
        var controller = CreateController(messageBus);

        var result = await controller.Start(jobId, CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        await messageBus.Received(1).RequestAsync<StartDownloadRequest, StartDownloadResponse>(
            DownloadSubjects.StartDownloadRequest,
            Arg.Is<StartDownloadRequest>(x => x.JobId == jobId && x.RequestedBy == "unit_test_user"),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    private static DownloadQueueController CreateController(IMessageBus messageBus)
    {
        var hub = new DownloadQueueHub(Substitute.For<IMessageBus>(), Substitute.For<ILogger<DownloadQueueHub>>());
        var controller = new DownloadQueueController(messageBus, hub, Substitute.For<ILogger<DownloadQueueController>>());

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, "unit_test_user")], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }
}
