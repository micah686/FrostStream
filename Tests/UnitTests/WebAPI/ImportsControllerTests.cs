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
using WebAPI.Features.Imports.Controllers;
using WebAPI.Features.Imports.Models;

namespace UnitTests.WebAPI;

public sealed class ImportsControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 12, 0);

    [Test]
    public async Task ImportLocalMedia_Publishes_Request()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        var result = await controller.ImportLocalMedia(new LocalMediaImportRequest
        {
            StorageKey = "storage-a",
            WorkerTag = " nas ",
            RequestedBy = "operator-note"
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<LocalMediaImportRequestResponse>();
        payload.BatchId.ShouldNotBe(Guid.Empty);
        payload.CorrelationId.ShouldNotBe(Guid.Empty);

        await publisher.Received(1).PublishAsync(
            LocalImportSubjects.LocalMediaImportRequested,
            Arg.Is<LocalMediaImportRequested>(x =>
                x.JobId == payload.BatchId &&
                x.CorrelationId == payload.CorrelationId &&
                x.OperationKey == $"local-import/{payload.BatchId:N}/requested" &&
                x.OccurredAt == Now &&
                x.StorageKey == "storage-a" &&
                x.WorkerTag == "nas" &&
                x.RequestedBy == "unit_test_user" &&
                x.RequestedByContext == "operator-note"),
            Arg.Is<string>(x => x.Length == 32),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImportLocalMedia_Rejects_Missing_StorageKey()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher);

        var result = await controller.ImportLocalMedia(new LocalMediaImportRequest
        {
            StorageKey = "  "
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(LocalMediaImportRequested)!, default, default, default);
    }

    private static ImportsController CreateController(IJetStreamPublisher publisher)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        var controller = new ImportsController(
            publisher,
            clock,
            Substitute.For<ILogger<ImportsController>>());

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, "unit_test_user")], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }
}
