using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shouldly;
using Shared.Database;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageCrudConsumerServiceTests
{
    [Test]
    public async Task ExecuteAsync_Subscribes_To_All_Storage_Subjects_Using_DataBridge_Queue_Group()
    {
        var bus = new FakeMessageBus();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), new InMemorySecretStore(), bus);
        var service = new StorageCrudConsumerService(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ISecretStore>(),
            SystemClock.Instance,
            Substitute.For<ILogger<StorageCrudConsumerService>>());

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);

        bus.Subscriptions.Keys.ToHashSet().SetEquals([
            StorageSubjects.CreateLocalStorage,
            StorageSubjects.CreateNetworkStorage,
            StorageSubjects.CreateS3CompatibleObjectStorage,
            StorageSubjects.CreateAzureBlobObjectStorage,
            StorageSubjects.CreateGoogleCloudStorageObjectStorage,
            StorageSubjects.UpdateLocalStorage,
            StorageSubjects.UpdateNetworkStorage,
            StorageSubjects.UpdateS3CompatibleObjectStorage,
            StorageSubjects.UpdateAzureBlobObjectStorage,
            StorageSubjects.UpdateGoogleCloudStorageObjectStorage,
            StorageSubjects.ListStorage,
            StorageSubjects.GetStorage,
            StorageSubjects.DeleteStorage
        ]).ShouldBeTrue();
        bus.Subscriptions.Values.All(x => x.QueueGroup == "databridge-storage").ShouldBeTrue();

        await service.StopAsync(CancellationToken.None);
        bus.Subscriptions.Values.All(x => x.Subscription.Stopped && x.Subscription.Disposed).ShouldBeTrue();
    }

    [Test]
    public async Task Create_List_Get_Update_And_Delete_Work_End_To_End_In_Process()
    {
        var bus = new FakeMessageBus();
        var secrets = new InMemorySecretStore();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), secrets, bus);
        var service = new StorageCrudConsumerService(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            secrets,
            SystemClock.Instance,
            Substitute.For<ILogger<StorageCrudConsumerService>>());
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);

        var create = await bus.InvokeAsync<StorageCreateStreamingRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.CreateNetworkStorage,
            new StorageCreateStreamingRequestMessage
            {
                Key = "network-a",
                Description = "desc",
                Parameters = new StreamingNetworkStorageParameters
                {
                    Protocol = NetworkStorageProtocol.Sftp,
                    Host = "example.test",
                    Port = 22,
                    Username = "micah",
                    Password = "pw",
                    BasePath = "/upload"
                }
            });
        create!.Success.ShouldBeTrue();
        create.Entity!.Network!.Host.ShouldBe("example.test");
        (await secrets.ReadAsync(SecretPaths.ForStorage("network-a")))![StorageSecretSplitter.NetworkPassword].ShouldBe("pw");

        var list = await bus.InvokeAsync<StorageListRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.ListStorage,
            new StorageListRequestMessage());
        list!.Items.ShouldHaveSingleItem();

        var get = await bus.InvokeAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.GetStorage,
            new StorageGetRequestMessage { Key = "network-a" });
        get!.Entity!.Network!.Username.ShouldBe("micah");

        var beforeUpdate = get.Entity.LastUpdated;
        await Task.Delay(5);

        var update = await bus.InvokeAsync<StorageUpdateStreamingRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.UpdateNetworkStorage,
            new StorageUpdateStreamingRequestMessage
            {
                Key = "network-a",
                Description = "changed",
                Parameters = new StreamingNetworkStorageParameters
                {
                    Protocol = NetworkStorageProtocol.Sftp,
                    Host = "changed.example.test",
                    Port = 2022,
                    Username = "micah",
                    PrivateKey = "private-key",
                    PublicKey = "public-key",
                    BasePath = "/changed"
                }
            });
        update!.Success.ShouldBeTrue();
        update.Entity!.Network!.Host.ShouldBe("changed.example.test");
        update.Entity.LastUpdated.ShouldNotBe(beforeUpdate);

        var delete = await bus.InvokeAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.DeleteStorage,
            new StorageDeleteRequestMessage { Key = "network-a" });
        delete!.Success.ShouldBeTrue();
        (await secrets.ReadAsync(SecretPaths.ForStorage("network-a"))).ShouldBeNull();

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        (await db.StorageConfigs.CountAsync()).ShouldBe(0);

        bus.PublishedMessages.Count(x => x.Subject == StorageSubjects.StorageConfigChanged).ShouldBe(3);
        bus.PublishedMessages.Select(x => x.Message).OfType<StorageConfigChangedMessage>().Select(x => x.Change)
            .ShouldBe([StorageConfigChangeKind.Created, StorageConfigChangeKind.Updated, StorageConfigChangeKind.Deleted]);

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Create_Rolls_Back_Secret_Write_When_Db_Save_Fails()
    {
        var bus = new FakeMessageBus();
        var secrets = new InMemorySecretStore();
        await using var services = StorageTestHelpers.BuildDbServices(
            Guid.NewGuid().ToString("n"),
            secrets,
            bus,
            new FailingSaveChangesInterceptor());
        var service = new StorageCrudConsumerService(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            secrets,
            SystemClock.Instance,
            Substitute.For<ILogger<StorageCrudConsumerService>>());
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);

        var response = await bus.InvokeAsync<StorageCreateStreamingRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.CreateNetworkStorage,
            new StorageCreateStreamingRequestMessage
            {
                Key = "network-a",
                Parameters = new StreamingNetworkStorageParameters
                {
                    Protocol = NetworkStorageProtocol.Sftp,
                    Host = "example.test",
                    Username = "micah",
                    Password = "pw"
                }
            });

        response!.Success.ShouldBeFalse();
        response.ErrorCode.ShouldBe("internal_error");
        secrets.Writes.ShouldContain(SecretPaths.ForStorage("network-a"));
        secrets.Deletes.ShouldContain(SecretPaths.ForStorage("network-a"));
        (await secrets.ReadAsync(SecretPaths.ForStorage("network-a"))).ShouldBeNull();
    }

    [Test]
    public async Task Default_Key_Is_Immutable_For_Update_And_Delete()
    {
        var bus = new FakeMessageBus();
        var secrets = new InMemorySecretStore();
        await using var services = StorageTestHelpers.BuildDbServices(Guid.NewGuid().ToString("n"), secrets, bus);

        await using (var seedScope = services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            var entity = new StorageConfigEntity
            {
                Key = "default",
                Description = "default"
            };
            entity.ApplyStoredParameters(new PosixLocalStorageStored
            {
                Protocol = LocalStorageProtocol.Local,
                Path = "/default"
            });
            db.StorageConfigs.Add(entity);
            await db.SaveChangesAsync();
        }

        var service = new StorageCrudConsumerService(
            bus,
            services.GetRequiredService<IServiceScopeFactory>(),
            secrets,
            SystemClock.Instance,
            Substitute.For<ILogger<StorageCrudConsumerService>>());
        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);

        var update = await bus.InvokeAsync<StorageUpdateLocalRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.UpdateLocalStorage,
            new StorageUpdateLocalRequestMessage
            {
                Key = "default",
                Parameters = new PosixLocalStorageParameters
                {
                    Protocol = LocalStorageProtocol.Local,
                    Path = "/changed"
                }
            });
        update!.ErrorCode.ShouldBe("forbidden");

        var delete = await bus.InvokeAsync<StorageDeleteRequestMessage, StorageOperationResponseMessage>(
            StorageSubjects.DeleteStorage,
            new StorageDeleteRequestMessage { Key = "default" });
        delete!.ErrorCode.ShouldBe("forbidden");
    }

    private static async Task WaitForSubscriptionsAsync(FakeMessageBus bus)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (bus.Subscriptions.Count == 13)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("StorageCrudConsumerService did not register all subscriptions in time.");
    }
}
