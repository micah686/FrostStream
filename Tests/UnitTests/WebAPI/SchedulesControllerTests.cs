using System.ComponentModel.DataAnnotations;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Controllers;

namespace UnitTests.WebAPI;

public sealed class SchedulesControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 14, 0);

    [Test]
    public async Task Create_Sends_Schedule_Message_And_Returns_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new SchedulesController(bus, Substitute.For<ILogger<SchedulesController>>());

        bus.RequestAsync<ScheduleCreateRequestMessage, ScheduleOperationResponseMessage>(
                ScheduleSubjects.Create,
                Arg.Is<ScheduleCreateRequestMessage>(x =>
                    x.Key == "daily-refresh" &&
                    x.TaskType == "channel_asset_refresh" &&
                    x.Cron == "0 0 3 ? * *" &&
                    x.IntervalSeconds == null &&
                    x.Timezone == "America/Los_Angeles" &&
                    x.Enabled &&
                    x.CatchupPolicy == ScheduleCatchupPolicy.Skip),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new ScheduleOperationResponseMessage
            {
                Success = true,
                Entity = CreateDto("daily-refresh")
            });

        var result = await controller.Create(new ScheduleCreateRequest
        {
            Key = "daily-refresh",
            TaskType = "channel_asset_refresh",
            Cron = "0 0 3 ? * *",
            Timezone = "America/Los_Angeles",
            Enabled = true,
            CatchupPolicy = ScheduleCatchupPolicy.Skip
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<ScheduledTaskResponse>();
        payload.Key.ShouldBe("daily-refresh");
        payload.Cron.ShouldBe("0 0 3 ? * *");
    }

    [Test]
    public async Task Delete_Maps_NotFound_To_404()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new SchedulesController(bus, Substitute.For<ILogger<SchedulesController>>());

        bus.RequestAsync<ScheduleDeleteRequestMessage, ScheduleOperationResponseMessage>(
                ScheduleSubjects.Delete,
                Arg.Is<ScheduleDeleteRequestMessage>(x => x.Key == "missing"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new ScheduleOperationResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Delete("missing", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    [Test]
    public void ScheduleCreateRequest_Requires_Exactly_One_Trigger()
    {
        var request = new ScheduleCreateRequest
        {
            Key = "daily-refresh",
            TaskType = "channel_asset_refresh",
            Cron = "0 0 3 ? * *",
            IntervalSeconds = 60,
            Timezone = "UTC"
        };

        var results = Validate(request);

        results.ShouldContain(x => x.ErrorMessage == "Exactly one of cron or intervalSeconds must be supplied.");
    }

    [Test]
    public void ScheduleCreateRequest_Rejects_Invalid_Timezone()
    {
        var request = new ScheduleCreateRequest
        {
            Key = "daily-refresh",
            TaskType = "channel_asset_refresh",
            IntervalSeconds = 60,
            Timezone = "Mars/Olympus"
        };

        var results = Validate(request);

        results.ShouldContain(x => x.ErrorMessage == "Timezone must be a valid TZDB timezone id.");
    }

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static ScheduledTaskDto CreateDto(string key) => new()
    {
        Id = 10,
        Key = key,
        TaskType = "channel_asset_refresh",
        Cron = "0 0 3 ? * *",
        Timezone = "America/Los_Angeles",
        Enabled = true,
        CatchupPolicy = ScheduleCatchupPolicy.Skip,
        CreatedAt = Now,
        LastUpdated = Now
    };
}
