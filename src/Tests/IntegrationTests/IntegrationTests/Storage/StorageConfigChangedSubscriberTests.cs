using IntegrationTests.Infrastructure;
using Shouldly;
using Shared.Messaging;
using Shared.Storage;
using System.Net;
using System.Net.Http.Json;
using TUnit.Core;
using WebAPI.Controllers;

namespace IntegrationTests.Storage;

public class StorageConfigChangedSubscriberTests
{
    private static readonly StorageStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static StorageConfigChangedSubscriberTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task ResetAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        await Fixture.ResetAsync();
    }

    [After(Test)]
    public void Release()
    {
        Gate.Release();
    }

    [Test]
    public async Task StorageConfigChanged_Publishes_Key_And_Change_Kind_On_Create_Update_Delete()
    {
        var messages = new List<StorageConfigChangedMessage>();
        var subscription = await Fixture.DataBridgeBus.SubscribeAsync<StorageConfigChangedMessage>(
            StorageSubjects.StorageConfigChanged,
            ctx =>
            {
                messages.Add(ctx.Message);
                return Task.CompletedTask;
            });
        await Task.Delay(500);

        (await Fixture.CreateLocalAsync("local-a", "/mnt/local-a")).Key.ShouldBe("local-a");
        (await Fixture.Client.PutAsJsonAsync("/api/storage/local/update/local-a", new LocalStorageUpdateRequest
        {
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/local-b"
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Fixture.Client.DeleteAsync("/api/storage/delete/local-a")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Task.Delay(1000);

        messages.Select(x => x.Key).ShouldAllBe(x => x == "local-a");
        messages.Select(x => x.Change).ShouldBe([
            StorageConfigChangeKind.Created,
            StorageConfigChangeKind.Updated,
            StorageConfigChangeKind.Deleted
        ]);

        await subscription.StopAsync();
        await subscription.DisposeAsync();
    }
}
