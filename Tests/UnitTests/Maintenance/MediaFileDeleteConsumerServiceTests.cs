using FluentStorage;
using FluentStorage.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;
using TUnit.Core;
using UnitTests.Storage;
using Worker.Services;

namespace UnitTests.Maintenance;

public sealed class MediaFileDeleteConsumerServiceTests
{
    [Test]
    public async Task Delete_Removes_File_From_Storage()
    {
        var storage = new FakeBlobStorage();
        storage.Write("media/video.mp4", "payload"u8.ToArray());
        var bus = new FakeMessageBus();
        var service = new MediaFileDeleteConsumerService(
            bus,
            new FakeBlobStorageProvider(storage),
            NullLogger<MediaFileDeleteConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);
        try
        {
            var response = await bus.InvokeAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(
                MediaFileSubjects.Delete,
                new DeleteMediaFileRequest { StorageKey = "storage-a", StoragePath = "media/video.mp4" });

            response.ShouldNotBeNull();
            response.Success.ShouldBeTrue();
            (await storage.ObjectExists("media/video.mp4")).ShouldBeFalse();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Delete_Of_Missing_Path_Is_Idempotent_Success()
    {
        var storage = new FakeBlobStorage();
        var bus = new FakeMessageBus();
        var service = new MediaFileDeleteConsumerService(
            bus,
            new FakeBlobStorageProvider(storage),
            NullLogger<MediaFileDeleteConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);
        try
        {
            var response = await bus.InvokeAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(
                MediaFileSubjects.Delete,
                new DeleteMediaFileRequest { StorageKey = "storage-a", StoragePath = "media/already-gone.mp4" });

            response.ShouldNotBeNull();
            response.Success.ShouldBeTrue();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Delete_Rejects_Missing_Storage_Key_And_Path()
    {
        var bus = new FakeMessageBus();
        var service = new MediaFileDeleteConsumerService(
            bus,
            new FakeBlobStorageProvider(new FakeBlobStorage()),
            NullLogger<MediaFileDeleteConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);
        try
        {
            var missingKey = await bus.InvokeAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(
                MediaFileSubjects.Delete,
                new DeleteMediaFileRequest { StorageKey = " ", StoragePath = "media/video.mp4" });

            missingKey.ShouldNotBeNull();
            missingKey.Success.ShouldBeFalse();
            missingKey.ErrorCode.ShouldBe("validation");

            var missingPath = await bus.InvokeAsync<DeleteMediaFileRequest, DeleteMediaFileResponse>(
                MediaFileSubjects.Delete,
                new DeleteMediaFileRequest { StorageKey = "storage-a", StoragePath = "" });

            missingPath.ShouldNotBeNull();
            missingPath.Success.ShouldBeFalse();
            missingPath.ErrorCode.ShouldBe("validation");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForSubscriptionsAsync(FakeMessageBus bus)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (bus.Subscriptions.Count == 1)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("MediaFileDeleteConsumerService did not register its subscription in time.");
    }

    private sealed class FakeBlobStorageProvider(FakeBlobStorage storage) : IBlobStorageProvider
    {
        public Task<IStore> GetAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult(storage.Store);

        public void Invalidate(string storageKey)
        {
        }
    }

    private sealed class FakeBlobStorage
    {
        public IStore Store { get; } = StorageFactory.InMemory();

        public void Write(string path, byte[] bytes)
            => Store.SetBytes(path, bytes).GetAwaiter().GetResult();

        public Task<bool> ObjectExists(string path, CancellationToken cancellationToken = default)
            => Store.ObjectExists(path, cancellationToken);
    }
}
