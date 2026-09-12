using Conduit.NATS;
using DataBridge.Application;
using DataBridge.Data;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Application;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Notes;

namespace UnitTests.DataBridge;

public sealed class UserNoteApplicationParityTests
{
    [Test]
    public async Task Direct_And_Full_Adapter_Return_Equivalent_Success()
    {
        var request = new UserNoteUpsertRequestMessage
        {
            OwnerSubject = "owner-a",
            TargetType = "video",
            TargetId = Guid.NewGuid().ToString(),
            Note = "phase six"
        };
        var entity = Note(request);
        var direct = CreateDirect(repository => repository.UpsertAsync(
                request.OwnerSubject,
                request.TargetType,
                request.TargetId,
                request.Note,
                Arg.Any<CancellationToken>())
            .Returns(UserNoteMutationResult.Ok(entity)));
        var expected = await direct.UpsertAsync(request);

        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<UserNoteUpsertRequestMessage, UserNoteResponseMessage>(
                UserNoteSubjects.Upsert,
                request,
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);
        var fullAdapter = new NatsUserNoteApplication(bus, Substitute.For<ILogger<NatsUserNoteApplication>>());

        var actual = await fullAdapter.UpsertAsync(request);

        actual.ShouldBeEquivalentTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task Direct_And_Full_Adapter_Preserve_Owner_Validation(string? owner)
    {
        var request = new UserNoteGetRequestMessage
        {
            OwnerSubject = owner!,
            TargetType = "video",
            TargetId = Guid.NewGuid().ToString()
        };
        var direct = CreateDirect();
        var expected = await direct.GetAsync(request);

        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<UserNoteGetRequestMessage, UserNoteResponseMessage>(
                UserNoteSubjects.Get,
                request,
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);
        var actual = await new NatsUserNoteApplication(
            bus,
            Substitute.For<ILogger<NatsUserNoteApplication>>()).GetAsync(request);

        actual.ShouldBeEquivalentTo(expected);
        actual!.ErrorCode.ShouldBe("validation");
    }

    [Test]
    public async Task Direct_Operation_Passes_Owner_And_Cancellation_To_Repository()
    {
        var repository = Substitute.For<IUserNotesRepository>();
        var application = new UserNoteApplication(repository, Substitute.For<ILogger<UserNoteApplication>>());
        using var cancellation = new CancellationTokenSource();
        var request = new UserNoteGetRequestMessage
        {
            OwnerSubject = "owner-b",
            TargetType = "channel",
            TargetId = "42"
        };

        await application.GetAsync(request, cancellation.Token);

        await repository.Received(1).GetAsync(
            "owner-b", "channel", "42", cancellation.Token);
    }

    [Test]
    public async Task Direct_And_Full_Adapter_Keep_Failure_Shapes()
    {
        var request = new UserNoteGetRequestMessage
        {
            OwnerSubject = "owner-a",
            TargetType = "video",
            TargetId = Guid.NewGuid().ToString()
        };
        var direct = CreateDirect(repository => repository.GetAsync(
                request.OwnerSubject,
                request.TargetType,
                request.TargetId,
                Arg.Any<CancellationToken>())
            .Returns<Task<UserNoteEntity?>>(_ => throw new InvalidOperationException("database offline")));
        var expected = await direct.GetAsync(request);

        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<UserNoteGetRequestMessage, UserNoteResponseMessage>(
                UserNoteSubjects.Get,
                request,
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);
        var actual = await new NatsUserNoteApplication(
            bus,
            Substitute.For<ILogger<NatsUserNoteApplication>>()).GetAsync(request);

        actual.ShouldBeEquivalentTo(expected);
        actual!.ErrorCode.ShouldBe("internal_error");
    }

    [Test]
    public async Task Full_Adapter_Maps_Transport_Failure_To_Unavailable()
    {
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<UserNoteGetRequestMessage, UserNoteResponseMessage>(
                Arg.Any<string>(),
                Arg.Any<UserNoteGetRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<UserNoteResponseMessage?>>(_ => throw new InvalidOperationException("nats offline"));
        var adapter = new NatsUserNoteApplication(bus, Substitute.For<ILogger<NatsUserNoteApplication>>());

        var response = await adapter.GetAsync(new UserNoteGetRequestMessage
        {
            OwnerSubject = "owner-a",
            TargetType = "video",
            TargetId = "target"
        });

        response.ShouldBeNull();
    }

    private static UserNoteApplication CreateDirect(Action<IUserNotesRepository>? configure = null)
    {
        var repository = Substitute.For<IUserNotesRepository>();
        configure?.Invoke(repository);
        return new UserNoteApplication(repository, Substitute.For<ILogger<UserNoteApplication>>());
    }

    private static UserNoteEntity Note(UserNoteUpsertRequestMessage request)
        => new()
        {
            OwnerSubject = request.OwnerSubject,
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            Note = request.Note
        };
}
