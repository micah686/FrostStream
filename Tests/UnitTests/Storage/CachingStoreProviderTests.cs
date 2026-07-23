using NSubstitute;
using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class CachingStoreProviderTests
{
    [Test]
    public async Task Caller_Cancellation_Does_Not_Poison_Shared_Store_Construction()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var pending = new TaskCompletionSource<StorageConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var configClient = Substitute.For<IStorageConfigClient>();
            configClient.GetStorageConfigAsync("local", CancellationToken.None).Returns(pending.Task);
            using var provider = new CachingStoreProvider(configClient);
            using var cancellation = new CancellationTokenSource();

            var cancelledWait = provider.GetAsync("local", cancellation.Token);
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(cancelledWait);

            pending.SetResult(LocalConfig(root));
            var storage = await provider.GetAsync("local");
            await storage.SetText("probe.txt", "ready");
            (await storage.GetText("probe.txt")).ShouldBe("ready");
            await configClient.Received(1).GetStorageConfigAsync("local", CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Failed_Construction_Is_Evicted_And_Retried()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configClient = Substitute.For<IStorageConfigClient>();
            configClient.GetStorageConfigAsync("local", CancellationToken.None)
                .Returns(StorageConfigResponse.NotFound("local"), LocalConfig(root));
            using var provider = new CachingStoreProvider(configClient);

            await Should.ThrowAsync<InvalidOperationException>(() => provider.GetAsync("local"));
            var storage = await provider.GetAsync("local");

            storage.ShouldNotBeNull();
            await configClient.Received(2).GetStorageConfigAsync("local", CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StorageConfigResponse LocalConfig(string path)
        => new(
            Found: true,
            Key: "local",
            Method: StorageMethod.Local,
            Parameters: StorageParametersSerializer.Serialize(
                StorageMethod.Local,
                new PosixLocalStorageParameters
                {
                    Protocol = LocalStorageProtocol.Local,
                    Path = path
                }),
            Description: null);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"froststream-store-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
