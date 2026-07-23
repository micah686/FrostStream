using FluentStorage.FTP.Storage;
using FluentStorage.SFTP;
using FluentStorage.Storage;
using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class FluentStoreFactoryTests
{
    [Test]
    public async Task CreateStorage_Creates_Usable_Local_Disk_Store()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            using var storage = Create(StorageMethod.Local, new PosixLocalStorageParameters
            {
                Protocol = LocalStorageProtocol.Local,
                Path = root
            });

            await storage.SetText("folder/probe.txt", "ready");
            (await storage.GetText("folder/probe.txt")).ShouldBe("ready");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CreateStorage_Uses_Disk_Store_For_Mounted_Network_Shares()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            foreach (var protocol in new[]
                     {
                         NetworkStorageProtocol.Nfs,
                         NetworkStorageProtocol.Smb,
                         NetworkStorageProtocol.Cifs
                     })
            {
                using var storage = Create(StorageMethod.Network, new StreamingNetworkStorageParameters
                {
                    Protocol = protocol,
                    Host = "fileserver.example.test",
                    BasePath = "/media",
                    MountPath = root
                });

                await storage.SetText($"{protocol}/probe.txt", "mounted");
                (await storage.GetText($"{protocol}/probe.txt")).ShouldBe("mounted");
                (await storage.IsFileSystem()).ShouldBeTrue();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CreateStorage_Rejects_Unmounted_Network_Share()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"froststream-missing-{Guid.NewGuid():N}");

        var exception = Should.Throw<DirectoryNotFoundException>(() =>
            Create(StorageMethod.Network, new StreamingNetworkStorageParameters
            {
                Protocol = NetworkStorageProtocol.Nfs,
                Host = "fileserver.example.test",
                MountPath = missing
            }));

        exception.Message.ShouldContain("not mounted");
    }

    [Test]
    public void CreateStorage_Uses_Typed_Ftp_And_Sftp_Stores()
    {
        using var ftp = Create(StorageMethod.Network, new StreamingNetworkStorageParameters
        {
            Protocol = NetworkStorageProtocol.Ftp,
            Host = "ftp.example.test",
            Port = 21,
            Username = "user",
            Password = "password"
        });
        using var sftp = Create(StorageMethod.Network, new StreamingNetworkStorageParameters
        {
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "sftp.example.test",
            Port = 22,
            Username = "user",
            Password = "password"
        });

        ftp.ShouldBeOfType<FtpStore>();
        sftp.ShouldBeOfType<SftpStore>();
    }

    [Test]
    public void CreateStorage_Uses_Dedicated_MinIo_Factory()
    {
        using var storage = Create(StorageMethod.ObjectStorage, new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.MinIo,
            BucketName = "bucket",
            Region = "us-east-1",
            Endpoint = "https://minio.example.test",
            AccessKeyId = "access",
            SecretKeyId = "secret",
            ForcePathStyle = true,
            UseSsl = true
        });

        storage.GetType().FullName.ShouldBe("FluentStorage.Minio.Storage.MinioStore");
    }

    [Test]
    public void StorageObjectPath_Uses_Unified_FluentStorage_Paths()
    {
        StorageObjectPath.Combine("media\\video", "hls", "index.m3u8")
            .ShouldBe("media/video/hls/index.m3u8");
        StorageObjectPath.GetParent("media/video/file.mp4")
            .ShouldBe("media/video");
        Should.Throw<ArgumentException>(() => StorageObjectPath.Combine("media", "..", "secret"));
    }

    [Test]
    [NotInParallel("FrostStreamStorageRootEnvironment")]
    public void CreateStorage_Requires_Configured_Shared_Storage_Root()
    {
        var previousRoot = Environment.GetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName, null);

        try
        {
            var exception = Should.Throw<InvalidOperationException>(() =>
                Create(StorageMethod.Local, new PosixLocalStorageParameters
                {
                    Protocol = LocalStorageProtocol.Local,
                    Path = LocalStoragePathResolver.StorageRootToken
                }));

            exception.Message.ShouldContain(LocalStoragePathResolver.EnvironmentVariableName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName, previousRoot);
        }
    }

    private static IStore Create(StorageMethod method, StorageParametersBase parameters)
    {
        return FluentStoreFactory.CreateStorage(new StorageConfigResponse(
            Found: true,
            Key: "test",
            Method: method,
            Parameters: StorageParametersSerializer.Serialize(method, parameters),
            Description: null));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"froststream-fluent-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
