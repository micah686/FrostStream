using MediaProcessor.Storage;
using NSubstitute;
using Shared.Storage;
using Shouldly;

namespace UnitTests.Storage;

public sealed class LocalMediaProcessorStorageClientTests
{
    [Test]
    public async Task Shared_Provider_Transfers_Media_Without_Http_And_Rejects_Traversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "storage"));
        try
        {
            using var store = FluentStoreFactory.CreateStorage(new StorageConfigResponse(true, "local", StorageMethod.Local,
                StorageParametersSerializer.Serialize(StorageMethod.Local, new PosixLocalStorageParameters
                    { Protocol = LocalStorageProtocol.Local, Path = Path.Combine(directory, "storage") }), null));
            var provider = Substitute.For<IStoreProvider>();
            provider.GetAsync("local", Arg.Any<CancellationToken>()).Returns(store);
            var client = new LocalMediaProcessorStorageClient(provider);
            var source = Path.Combine(directory, "source.bin");
            var target = Path.Combine(directory, "target.bin");
            var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            await client.UploadFromFileAsync(source, "local", "media/file.bin", CancellationToken.None);
            await client.DownloadToFileAsync("local", "media/file.bin", target, CancellationToken.None);
            (await File.ReadAllBytesAsync(target)).ShouldBe(bytes);
            await Should.ThrowAsync<ArgumentException>(() => client.DownloadToFileAsync("local", "../escape", target, CancellationToken.None));
            await Should.ThrowAsync<ArgumentException>(() => client.UploadFromFileAsync(source, "local", "/escape", CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }
}
