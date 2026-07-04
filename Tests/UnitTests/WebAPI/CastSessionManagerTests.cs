using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Media.Casting;
using CastMedia = Sharpcaster.Models.Media.Media;

namespace UnitTests.WebAPI;

public sealed class CastSessionManagerTests
{
    private const string DeviceId = "abcdef0123456789";
    private static readonly Guid MediaGuid = Guid.NewGuid();

    [Test]
    public async Task Start_Connects_Launches_And_Loads()
    {
        var fixture = CreateFixture();

        var session = await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);

        Received.InOrder(() =>
        {
            fixture.Client.ConnectAsync(Arg.Any<ChromecastReceiver>());
            fixture.Client.LaunchDefaultReceiverAsync();
            fixture.Client.LoadAsync(Arg.Any<CastMedia>(), Arg.Any<int[]?>());
        });
        session.DeviceId.ShouldBe(DeviceId);
        session.MediaGuid.ShouldBe(MediaGuid);
        session.Title.ShouldBe("Test Movie");
        fixture.Manager.ListSessions().ShouldHaveSingleItem();
        fixture.Manager.GetSession(DeviceId).ShouldNotBeNull();
    }

    [Test]
    public async Task Start_With_Position_Seeks_After_Load()
    {
        var fixture = CreateFixture();

        await fixture.Manager.StartAsync(DeviceId, Spec() with { StartPositionSeconds = 90 }, CancellationToken.None);

        await fixture.Client.Received(1).SeekAsync(90);
    }

    [Test]
    public async Task Second_Start_On_Same_Device_Reuses_The_Connection()
    {
        var fixture = CreateFixture();

        await fixture.Manager.StartAsync(DeviceId, Spec(), CancellationToken.None);
        var otherMedia = Guid.NewGuid();
        var session = await fixture.Manager.StartAsync(
            DeviceId, Spec() with { MediaGuid = otherMedia, Title = "Second" }, CancellationToken.None);

        fixture.Factory.Received(1).Create();
        await fixture.Client.Received(1).ConnectAsync(Arg.Any<ChromecastReceiver>());
        await fixture.Client.Received(2).LoadAsync(Arg.Any<CastMedia>(), Arg.Any<int[]?>());
        session.MediaGuid.ShouldBe(otherMedia);
        session.Title.ShouldBe("Second");
        fixture.Manager.ListSessions().ShouldHaveSingleItem();
    }

    [Test]
    public async Task Failed_Connect_Leaves_No_Session_And_Disposes_The_Client()
    {
        var fixture = CreateFixture();
        fixture.Client.ConnectAsync(Arg.Any<ChromecastReceiver>()).ThrowsAsync(new IOException("connection refused"));

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
        fixture.Client.PauseAsync().Returns(Status(PlayerStateType.Paused, currentTime: 42));

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
        fixture.Client.GetMediaStatusAsync().Returns(Status(PlayerStateType.Paused, currentTime: 12));
        fixture.Client.PlayAsync().Returns(Status(PlayerStateType.Playing, currentTime: 12));

        var session = await fixture.Manager.PlayAsync(DeviceId, CancellationToken.None);

        await fixture.Client.Received(1).GetMediaStatusAsync();
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
        fixture.Client.GetMediaStatusAsync().Returns((MediaStatus?)null);

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

        fixture.Client.MediaStatusChanged += Raise.Event<EventHandler<MediaStatus?>>(
            fixture.Client, Status(PlayerStateType.Playing, currentTime: 12));

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
        ICastSessionClientFactory Factory,
        ICastDeviceLocator Locator);

    private static Fixture CreateFixture()
    {
        var receiver = new ChromecastReceiver { DeviceUri = new Uri("https://192.168.1.50"), Name = "Living Room TV", Port = 8009 };
        var device = new CastDeviceDto { Id = DeviceId, Name = "Living Room TV", Host = "192.168.1.50", Port = 8009 };

        var locator = Substitute.For<ICastDeviceLocator>();
        locator.GetDevicesAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([device]);
        locator.FindReceiverAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(receiver);
        locator.FindReceiverAsync(Arg.Is<string>(id => id != DeviceId), Arg.Any<CancellationToken>())
            .Returns((ChromecastReceiver?)null);

        var client = Substitute.For<ICastSessionClient>();
        client.HasMediaSession.Returns(true);
        client.GetMediaStatusAsync()
            .Returns(Status(PlayerStateType.Buffering, currentTime: 0));
        client.LoadAsync(Arg.Any<CastMedia>(), Arg.Any<int[]?>())
            .Returns(Status(PlayerStateType.Buffering, currentTime: 0));
        client.SeekAsync(Arg.Any<double>())
            .Returns(callInfo => Status(PlayerStateType.Playing, currentTime: callInfo.Arg<double>()));

        var factory = Substitute.For<ICastSessionClientFactory>();
        factory.Create().Returns(client);

        var manager = new CastSessionManager(locator, factory, Substitute.For<ILogger<CastSessionManager>>());
        return new Fixture(manager, client, factory, locator);
    }

    private static CastLoadSpec Spec() => new()
    {
        MediaGuid = MediaGuid,
        Title = "Test Movie",
        Media = new CastMedia
        {
            ContentUrl = "http://192.168.1.10:5041/api/watch/x?castToken=abc",
            ContentType = "video/mp4",
            StreamType = StreamType.Buffered
        },
        TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(4)
    };

    private static MediaStatus Status(PlayerStateType state, double currentTime) => new()
    {
        PlayerState = state,
        CurrentTime = currentTime,
        Media = new CastMedia { Duration = 600 }
    };

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;
}
