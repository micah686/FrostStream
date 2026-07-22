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

public sealed class OrphanCleanupConsumerServiceTests
{
    [Test]
    public async Task RestoreFile_Copies_From_Orphan_Path_And_Deletes_Source()
    {
        var storage = new FakeBlobStorage();
        storage.Write("orphaned/20260501/1/video.mp4", "restored"u8.ToArray());
        var bus = new FakeMessageBus();
        var service = new OrphanCleanupConsumerService(
            bus,
            new FakeBlobStorageProvider(storage),
            NullLogger<OrphanCleanupConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);
        try
        {
            var response = await bus.InvokeAsync<RestoreOrphanedFileRequest, RestoreOrphanedFileResponse>(
                OrphanCleanupSubjects.RestoreFile,
                new RestoreOrphanedFileRequest
                {
                    OrphanId = 1,
                    StorageKey = "storage-a",
                    OrphanStoragePath = "orphaned/20260501/1/video.mp4",
                    OriginalStoragePath = "media/video.mp4"
                });

            response.ShouldNotBeNull();
            response.Success.ShouldBeTrue();
            (await storage.ObjectExists("orphaned/20260501/1/video.mp4")).ShouldBeFalse();
            storage.Read("media/video.mp4").ShouldBe("restored"u8.ToArray());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task RestoreFile_Rejects_Invalid_Paths()
    {
        var bus = new FakeMessageBus();
        var service = new OrphanCleanupConsumerService(
            bus,
            new FakeBlobStorageProvider(new FakeBlobStorage()),
            NullLogger<OrphanCleanupConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);
        try
        {
            var invalidSource = await bus.InvokeAsync<RestoreOrphanedFileRequest, RestoreOrphanedFileResponse>(
                OrphanCleanupSubjects.RestoreFile,
                new RestoreOrphanedFileRequest
                {
                    OrphanId = 1,
                    StorageKey = "storage-a",
                    OrphanStoragePath = "media/video.mp4",
                    OriginalStoragePath = "restored/video.mp4"
                });

            invalidSource.ShouldNotBeNull();
            invalidSource.Success.ShouldBeFalse();
            invalidSource.ErrorCode.ShouldBe("validation");

            var invalidDestination = await bus.InvokeAsync<RestoreOrphanedFileRequest, RestoreOrphanedFileResponse>(
                OrphanCleanupSubjects.RestoreFile,
                new RestoreOrphanedFileRequest
                {
                    OrphanId = 1,
                    StorageKey = "storage-a",
                    OrphanStoragePath = "orphaned/20260501/1/video.mp4",
                    OriginalStoragePath = "orphaned/restored/video.mp4"
                });

            invalidDestination.ShouldNotBeNull();
            invalidDestination.Success.ShouldBeFalse();
            invalidDestination.ErrorCode.ShouldBe("validation");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task RestoreFile_Destination_Conflict_Leaves_Source_And_Destination_Unchanged()
    {
        var storage = new FakeBlobStorage();
        storage.Write("orphaned/20260501/1/video.mp4", "source"u8.ToArray());
        storage.Write("media/video.mp4", "destination"u8.ToArray());
        var bus = new FakeMessageBus();
        var service = new OrphanCleanupConsumerService(
            bus,
            new FakeBlobStorageProvider(storage),
            NullLogger<OrphanCleanupConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(bus);
        try
        {
            var response = await bus.InvokeAsync<RestoreOrphanedFileRequest, RestoreOrphanedFileResponse>(
                OrphanCleanupSubjects.RestoreFile,
                new RestoreOrphanedFileRequest
                {
                    OrphanId = 1,
                    StorageKey = "storage-a",
                    OrphanStoragePath = "orphaned/20260501/1/video.mp4",
                    OriginalStoragePath = "media/video.mp4"
                });

            response.ShouldNotBeNull();
            response.Success.ShouldBeFalse();
            response.ErrorCode.ShouldBe("conflict");
            storage.Read("orphaned/20260501/1/video.mp4").ShouldBe("source"u8.ToArray());
            storage.Read("media/video.mp4").ShouldBe("destination"u8.ToArray());
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
            if (bus.Subscriptions.Count == 4)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("OrphanCleanupConsumerService did not register all subscriptions in time.");
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

        public byte[] Read(string path)
            => Store.GetBytes(path).GetAwaiter().GetResult();

        public Task<bool> ObjectExists(string path, CancellationToken cancellationToken = default)
            => Store.ObjectExists(path, cancellationToken);
    }
}
