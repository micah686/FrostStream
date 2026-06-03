using FluentStorage;
using FluentStorage.Blobs;
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
            (await storage.ExistsAsync("orphaned/20260501/1/video.mp4")).ShouldBeFalse();
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

    private sealed class FakeBlobStorageProvider(IBlobStorage storage) : IBlobStorageProvider
    {
        public Task<IBlobStorage> GetAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult(storage);

        public void Invalidate(string storageKey)
        {
        }
    }

    private sealed class FakeBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public void Write(string path, byte[] bytes)
            => _files[path] = bytes;

        public byte[] Read(string path)
            => _files[path];

        public Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Blob>>([]);

        public async Task WriteAsync(
            string fullPath,
            Stream dataStream,
            bool append = false,
            CancellationToken cancellationToken = default)
        {
            await using var memory = new MemoryStream();
            await dataStream.CopyToAsync(memory, cancellationToken);
            _files[fullPath] = memory.ToArray();
        }

        public Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(_files[fullPath], writable: false));

        public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        {
            foreach (var path in fullPaths)
            {
                _files.Remove(path);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<bool>> ExistsAsync(
            IEnumerable<string> fullPaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<bool>>(fullPaths.Select(path => _files.ContainsKey(path)).ToList());

        public Task<IReadOnlyCollection<Blob?>> GetBlobsAsync(
            IEnumerable<string> fullPaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Blob?>>(fullPaths
                .Select(path => _files.ContainsKey(path) ? new Blob(path) : null)
                .ToList());

        public Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ITransaction> OpenTransactionAsync()
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
