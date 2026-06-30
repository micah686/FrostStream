using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.OptionPresets.Controllers;
using WebAPI.Features.OptionPresets.Models;
using YtDlpSharpLib.Options;

namespace UnitTests.WebAPI;

public sealed class OptionPresetsControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 13, 0);

    [Test]
    public async Task Create_Sends_Serialized_Options_And_Returns_Response()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new OptionPresetsController(bus, Substitute.For<ILogger<OptionPresetsController>>());
        var dto = CreateDto("audio-high", """{"Verbose":true}""");

        bus.RequestAsync<OptionPresetCreateRequestMessage, OptionPresetOperationResponseMessage>(
                OptionPresetSubjects.CreatePreset,
                Arg.Is<OptionPresetCreateRequestMessage>(x =>
                    x.Key == "audio-high" &&
                    x.Name == "Audio High" &&
                    x.Description == "desc" &&
                    !string.IsNullOrWhiteSpace(x.YtDlpOptionsJson)),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new OptionPresetOperationResponseMessage { Success = true, Entity = dto });

        var result = await controller.Create(new OptionPresetCreateRequest
        {
            Key = "audio-high",
            Name = "Audio High",
            Description = "desc",
            YtDlpOptions = new YtDlpOptions()
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<OptionPresetResponse>();
        payload.Key.ShouldBe("audio-high");
        payload.Name.ShouldBe("Audio High");
        payload.YtDlpOptions.ShouldNotBeNull();
    }

    [Test]
    public async Task Get_Maps_NotFound_To_404()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new OptionPresetsController(bus, Substitute.For<ILogger<OptionPresetsController>>());

        bus.RequestAsync<OptionPresetGetRequestMessage, OptionPresetOperationResponseMessage>(
                OptionPresetSubjects.GetPreset,
                Arg.Is<OptionPresetGetRequestMessage>(x => x.Key == "missing"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new OptionPresetOperationResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var result = await controller.Get("missing", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    [Test]
    public async Task List_Maps_Invalid_Stored_Json_To_Empty_Options()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new OptionPresetsController(bus, Substitute.For<ILogger<OptionPresetsController>>());

        bus.RequestAsync<OptionPresetListRequestMessage, OptionPresetOperationResponseMessage>(
                OptionPresetSubjects.ListPresets,
                Arg.Any<OptionPresetListRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new OptionPresetOperationResponseMessage
            {
                Success = true,
                Items = [CreateDto("broken", "{not-json")]
            });

        var result = await controller.List(CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeAssignableTo<IReadOnlyCollection<OptionPresetResponse>>();
        payload.ShouldNotBeNull();
        payload.Single().YtDlpOptions.ShouldNotBeNull();
    }

    [Test]
    public async Task Delete_Maps_Conflict_To_409()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new OptionPresetsController(bus, Substitute.For<ILogger<OptionPresetsController>>());

        bus.RequestAsync<OptionPresetDeleteRequestMessage, OptionPresetOperationResponseMessage>(
                OptionPresetSubjects.DeletePreset,
                Arg.Is<OptionPresetDeleteRequestMessage>(x => x.Key == "in-use"),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new OptionPresetOperationResponseMessage
            {
                Success = false,
                ErrorCode = "conflict",
                ErrorMessage = "in use"
            });

        var result = await controller.Delete("in-use", CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>().Value.ShouldBe("in use");
    }

    [Test]
    public async Task Create_Returns_503_When_Bus_Throws()
    {
        var bus = Substitute.For<IMessageBus>();
        var controller = new OptionPresetsController(bus, Substitute.For<ILogger<OptionPresetsController>>());

        bus.RequestAsync<OptionPresetCreateRequestMessage, OptionPresetOperationResponseMessage>(
                Arg.Any<string>(),
                Arg.Any<OptionPresetCreateRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OptionPresetOperationResponseMessage?>(
                new InvalidOperationException("nats unavailable")));

        var result = await controller.Create(new OptionPresetCreateRequest
        {
            Key = "audio-high",
            Name = "Audio High",
            YtDlpOptions = new YtDlpOptions()
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    private static OptionPresetDto CreateDto(string key, string json) => new()
    {
        Id = 1,
        Key = key,
        Name = "Audio High",
        Description = "desc",
        YtDlpOptionsJson = json,
        CreatedAt = Now,
        LastUpdated = Now
    };
}
