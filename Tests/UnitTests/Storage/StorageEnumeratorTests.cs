using FluentStorage.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Shared.Storage;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Storage;

public class StorageEnumeratorTests
{
    [Test]
    public async Task EnumerateFilePathsAsync_Streams_Local_Files_Relative_To_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"froststream-storage-enumerator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "media", "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "media", "one.mp4"), "one");
        await File.WriteAllTextAsync(Path.Combine(root, "media", "nested", "two.mp4"), "two");

        try
        {
            var storageKey = "local-test";
            var configClient = Substitute.For<IStorageConfigClient>();
            configClient.GetStorageConfigAsync(storageKey, Arg.Any<CancellationToken>())
                .Returns(new StorageConfigResponse(
                    Found: true,
                    Key: storageKey,
                    Method: StorageMethod.Local,
                    Parameters: StorageParametersSerializer.Serialize(
                        StorageMethod.Local,
                        new PosixLocalStorageParameters
                        {
                            Protocol = LocalStorageProtocol.Local,
                            Path = root
                        }),
                    Description: null));

            var fallback = Substitute.For<IBlobStorageProvider>();
            var sut = new StorageEnumerator(configClient, fallback, NullLogger<StorageEnumerator>.Instance);

            var paths = new List<string>();
            await foreach (var path in sut.EnumerateFilePathsAsync(storageKey))
            {
                paths.Add(path);
            }

            paths.Order(StringComparer.Ordinal).ShouldBe([
                "media/nested/two.mp4",
                "media/one.mp4"
            ]);

            await fallback.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [NotInParallel("FrostStreamStorageRootEnvironment")]
    public async Task EnumerateFilePathsAsync_Resolves_Shared_Storage_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"froststream-storage-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "video.mp4"), "video");

        var previousRoot = Environment.GetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName, root);

        try
        {
            var storageKey = "local-test";
            var configClient = Substitute.For<IStorageConfigClient>();
            configClient.GetStorageConfigAsync(storageKey, Arg.Any<CancellationToken>())
                .Returns(new StorageConfigResponse(
                    Found: true,
                    Key: storageKey,
                    Method: StorageMethod.Local,
                    Parameters: StorageParametersSerializer.Serialize(
                        StorageMethod.Local,
                        new PosixLocalStorageParameters
                        {
                            Protocol = LocalStorageProtocol.Local,
                            Path = LocalStoragePathResolver.StorageRootToken
                        }),
                    Description: null));

            var sut = new StorageEnumerator(
                configClient,
                Substitute.For<IBlobStorageProvider>(),
                NullLogger<StorageEnumerator>.Instance);

            var paths = new List<string>();
            await foreach (var path in sut.EnumerateFilePathsAsync(storageKey))
            {
                paths.Add(path);
            }

            paths.ShouldBe(["video.mp4"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName, previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }
}
