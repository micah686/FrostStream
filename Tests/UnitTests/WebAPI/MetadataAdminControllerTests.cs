using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Metadata.Controllers;

namespace UnitTests.WebAPI;

public sealed class MetadataAdminControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 18, 0);

    [Test]
    public async Task TriggerReindex_Publishes_Manual_Search_Reindex_Request()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        var result = await controller.TriggerReindex(CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        await publisher.Received(1).PublishAsync(
            BackgroundJobSubjects.SearchReindexRequest,
            Arg.Is<SearchReindexRequested>(x => x != null &&
                x.ScheduleKey == BackgroundJobRequestFactory.ManualScheduleKey &&
                x.TaskType == BackgroundJobRequestFactory.ManualSearchReindexTaskType &&
                x.DueWindowUtc == Now &&
                x.OccurredAt == Now &&
                x.IdempotencyKey == "manual_search_reindex:manual:2026-06-03T18:00:00Z"),
            "manual_search_reindex:manual:2026-06-03T18:00:00Z",
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TriggerReindex_Returns_503_When_Publish_Fails()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        publisher.PublishAsync(
                Arg.Any<string>(),
                Arg.Any<SearchReindexRequested>(),
                Arg.Any<string>(),
                null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("nats unavailable")));

        var result = await controller.TriggerReindex(CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task TriggerDatabaseReindex_Publishes_Manual_Database_Reindex_Request()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var controller = CreateController(publisher: publisher);

        var result = await controller.TriggerDatabaseReindex(CancellationToken.None);

        result.ShouldBeOfType<AcceptedResult>();
        await publisher.Received(1).PublishAsync(
            BackgroundJobSubjects.DatabaseMaintenanceReindexRequest,
            Arg.Is<DatabaseMaintenanceReindexRequested>(x => x != null &&
                x.ScheduleKey == BackgroundJobRequestFactory.ManualScheduleKey &&
                x.TaskType == BackgroundJobRequestFactory.ManualDatabaseMaintenanceReindexTaskType &&
                x.DueWindowUtc == Now &&
                x.OccurredAt == Now &&
                x.IdempotencyKey == "manual_database_maintenance_reindex:manual:2026-06-03T18:00:00Z"),
            "manual_database_maintenance_reindex:manual:2026-06-03T18:00:00Z",
            null,
            Arg.Any<CancellationToken>());
    }

    private static MetadataAdminController CreateController(
        IJetStreamPublisher? publisher = null,
        IMessageBus? bus = null)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        return new MetadataAdminController(
            publisher ?? Substitute.For<IJetStreamPublisher>(),
            bus ?? Substitute.For<IMessageBus>(),
            clock,
            Substitute.For<ILogger<MetadataAdminController>>());
    }
}
