using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Media.Casting;

namespace UnitTests.WebAPI;

public sealed class CastSessionManagerTests
{
    private const string DeviceId = "chromecast:abcdef0123456789";
    private static readonly Guid MediaGuid = Guid.NewGuid();

    [Test]
    public async Task Start_Connects_Launches_And_Loads()
    {
        var fixture = CreateFixture();

        var session = await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);

        Received.InOrder(() =>
        {
            fixture.Client.ConnectAsync();
            fixture.Client.LoadAsync(Arg.Any<CastLoadSpec>());
        });
        session.DeviceId.ShouldBe(DeviceId);
        session.MediaGuid.ShouldBe(MediaGuid);
        session.Title.ShouldBe("Test Movie");
        fixture.Manager.ListSessions().ShouldHaveSingleItem();
        fixture.Manager.GetSession(DeviceId).ShouldNotBeNull();
    }

    [Test]
    public async Task Start_With_Position_Passes_Position_To_Client()
    {
        var fixture = CreateFixture();

        await fixture.Manager.StartAsync(DeviceId, Spec() with { StartPositionSeconds = 90 }, CancellationToken.None);

        await fixture.Client.Received(1).LoadAsync(Arg.Is<CastLoadSpec>(spec => spec != null && spec.StartPositionSeconds == 90));
    }

    [Test]
    public async Task Second_Start_On_Same_Device_Reuses_The_Connection()
    {
        var fixture = CreateFixture();

        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        var otherMedia = Guid.NewGuid();
        var session = await fixture.Manager.StartAsync(
            DeviceId, Spec() with { MediaGuid = otherMedia, Title = "Second" }, CancellationToken.None);

        await fixture.Registry.Received(1).CreateClientAsync(DeviceId, Arg.Any<CancellationToken>());
        await fixture.Client.Received(1).ConnectAsync();
        await fixture.Client.Received(2).LoadAsync(Arg.Any<CastLoadSpec>());
        session.MediaGuid.ShouldBe(otherMedia);
        session.Title.ShouldBe("Second");
        fixture.Manager.ListSessions().ShouldHaveSingleItem();
    }

    [Test]
    public async Task Failed_Connect_Leaves_No_Session_And_Disposes_The_Client()
    {
        var fixture = CreateFixture();
        fixture.Client.ConnectAsync().ThrowsAsync(new IOException("connection refused"));

        await Should.ThrowAsync<CastDeviceUnreachableException>(
            () => fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None));

        fixture.Manager.ListSessions().ShouldBeEmpty();
        await fixture.Client.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Unknown_Device_Fails_Start_And_Transport_Commands()
    {
        var fixture = CreateFixture();

        await Should.ThrowAsync<CastDeviceNotFoundException>(
            () => fixture.Manager.StartAsync("missing", Spec(), CancellationToken.None));
        await Should.ThrowAsync<CastSessionNotFoundException>(
            () => fixture.Manager.PlayAsync("missing", CancellationToken.None));
        await Should.ThrowAsync<CastSessionNotFoundException>(
            () => fixture.Manager.SeekAsync("missing", 10, CancellationToken.None));
        fixture.Manager.Subscribe("missing").ShouldBeNull();
    }

    [Test]
    public async Task Transport_Commands_Update_The_Snapshot_From_Media_Status()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        fixture.Client.PauseAsync().Returns(Status("Paused", currentTime: 42));

        var session = await fixture.Manager.PauseAsync(DeviceId, CancellationToken.None);

        session.Snapshot.PlayerState.ShouldBe("Paused");
        session.Snapshot.CurrentTime.ShouldBe(42);
    }

    [Test]
    public async Task Transport_Command_Refreshes_Missing_Media_Session_Before_Command()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        fixture.Client.HasMediaSession.Returns(false, true);
        fixture.Client.GetStatusAsync().Returns(Status("Paused", currentTime: 12));
        fixture.Client.PlayAsync().Returns(Status("Playing", currentTime: 12));

        var session = await fixture.Manager.PlayAsync(DeviceId, CancellationToken.None);

        await fixture.Client.Received(1).GetStatusAsync();
        await fixture.Client.Received(1).PlayAsync();
        session.Snapshot.PlayerState.ShouldBe("Playing");
        session.Snapshot.CurrentTime.ShouldBe(12);
    }

    [Test]
    public async Task Transport_Command_Fails_Clearly_When_Refresh_Finds_No_Media_Session()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        fixture.Client.HasMediaSession.Returns(false);
        fixture.Client.GetStatusAsync().Returns((CastSessionSnapshot?)null);

        var error = await Should.ThrowAsync<CastDeviceUnreachableException>(
            () => fixture.Manager.PlayAsync(DeviceId, CancellationToken.None));

        error.Message.ShouldBe("No media session is active on 'Living Room TV'. Start casting the media again.");
        await fixture.Client.DidNotReceive().PlayAsync();
    }

    [Test]
    public async Task Subscribe_Delivers_The_Current_Snapshot_Immediately()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);

        var subscription = fixture.Manager.Subscribe(DeviceId);

        subscription.ShouldNotBeNull();
        var evt = await subscription.Value.Reader.ReadAsync(TestToken());
        evt.Name.ShouldBe(CastSessionEvent.StatusEvent);
        evt.Session.MediaGuid.ShouldBe(MediaGuid);
    }

    [Test]
    public async Task Receiver_Status_Push_Fans_A_Status_Event_To_Subscribers()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        var subscription = fixture.Manager.Subscribe(DeviceId)!.Value;
        await subscription.Reader.ReadAsync(TestToken()); // drain the on-subscribe snapshot

        fixture.Client.StatusChanged += Raise.Event<EventHandler<CastSessionSnapshot?>>(
            fixture.Client, Status("Playing", currentTime: 12));

        var evt = await subscription.Reader.ReadAsync(TestToken());
        evt.Name.ShouldBe(CastSessionEvent.StatusEvent);
        evt.Session.Snapshot.PlayerState.ShouldBe("Playing");
        evt.Session.Snapshot.CurrentTime.ShouldBe(12);
    }

    [Test]
    public async Task Device_Disconnect_Ends_The_Session_And_Completes_Subscribers()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        var subscription = fixture.Manager.Subscribe(DeviceId)!.Value;
        await subscription.Reader.ReadAsync(TestToken()); // drain the on-subscribe snapshot

        fixture.Client.Disconnected += Raise.Event<EventHandler>(fixture.Client, EventArgs.Empty);

        var evt = await subscription.Reader.ReadAsync(TestToken());
        evt.Name.ShouldBe(CastSessionEvent.EndedEvent);
        evt.Session.Snapshot.PlayerState.ShouldBe("Disconnected");
        (await subscription.Reader.WaitToReadAsync(TestToken())).ShouldBeFalse();
        fixture.Manager.ListSessions().ShouldBeEmpty();
    }

    [Test]
    public async Task Explicit_Disconnect_Tears_Down_The_Session()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        var subscription = fixture.Manager.Subscribe(DeviceId)!.Value;

        await fixture.Manager.DisconnectAsync(DeviceId);

        await fixture.Client.Received(1).DisconnectAsync();
        await fixture.Client.Received(1).DisposeAsync();
        fixture.Manager.ListSessions().ShouldBeEmpty();
        // Reader ends after the queued snapshot + ended frames drain.
        var sawEnded = false;
        while (await subscription.Reader.WaitToReadAsync(TestToken()))
        {
            while (subscription.Reader.TryRead(out var evt))
                sawEnded |= evt.Name == CastSessionEvent.EndedEvent;
        }
        sawEnded.ShouldBeTrue();
    }

    [Test]
    public async Task Volume_Command_Applies_Level_And_Mute()
    {
        var fixture = CreateFixture();
        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);

        var session = await fixture.Manager.SetVolumeAsync(DeviceId, 0.5, true, CancellationToken.None);

        await fixture.Client.Received(1).SetVolumeAsync(0.5);
        await fixture.Client.Received(1).SetMutedAsync(true);
        session.Snapshot.VolumeLevel.ShouldBe(0.5);
        session.Snapshot.Muted.ShouldBe(true);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────

    private sealed record Fixture(
        CastSessionManager Manager,
        ICastSessionClient Client,
        ICastDeviceRegistry Registry);

    private static Fixture CreateFixture()
    {
        var device = new CastDeviceDto
        {
            Id = DeviceId,
            Protocol = CastProtocolIds.Chromecast,
            Name = "Living Room TV",
            Host = "192.168.1.50",
            Port = 8009
        };

        var registry = Substitute.For<ICastDeviceRegistry>();
        registry.GetDevicesAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([device]);

        var client = Substitute.For<ICastSessionClient>();
        client.HasMediaSession.Returns(true);
        client.GetStatusAsync()
            .Returns(Status("Buffering", currentTime: 0));
        client.LoadAsync(Arg.Any<CastLoadSpec>())
            .Returns(Status("Buffering", currentTime: 0));
        client.SeekAsync(Arg.Any<double>())
            .Returns(callInfo => Status("Playing", currentTime: callInfo.Arg<double>()));
        registry.CreateClientAsync(DeviceId, Arg.Any<CancellationToken>())
            .Returns(new CastSessionClientHandle(device, client));
        registry.CreateClientAsync(Arg.Is<string>(id => id != null && id != DeviceId), Arg.Any<CancellationToken>())
            .Returns((CastSessionClientHandle?)null);

        var manager = new CastSessionManager(registry, Substitute.For<ILogger<CastSessionManager>>());
        return new Fixture(manager, client, registry);
    }

    private static CastLoadSpec Spec() => new()
    {
        MediaGuid = MediaGuid,
        Title = "Test Movie",
        ContentUrl = "http://192.168.1.10:5041/api/media/watch/x?castToken=abc",
        ContentType = "video/mp4",
        DurationSeconds = 600,
        TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(4)
    };

    private static CastSessionSnapshot Status(string state, double currentTime) => new()
    {
        PlayerState = state,
        CurrentTime = currentTime,
        DurationSeconds = 600,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;
}
