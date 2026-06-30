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
            (await storage.ExistsAsync("media/video.mp4")).ShouldBeFalse();
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
