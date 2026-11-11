using FluentStorage;
using FluentStorage.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Shouldly;
using StorageExtensions.Nfs;
using StorageExtensions.Smb;
using TUnit.Core;
using TUnit.Core.Enums;

namespace IntegrationTests.Storage;

/// <summary>
/// Exercises the direct NFSv3 and SMB providers against disposable protocol servers.
/// SMB and NFS are started independently so rootless runtimes can run the SMB test
/// in a private network even when they cannot publish privileged ports.
/// </summary>
[NotInParallel("StorageExtensionsContainers")]
public class StorageExtensionsProviderTests
{
    private static readonly StorageProtocolContainerFixture Fixture = new();

    static StorageExtensionsProviderTests()
        => AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Test]
    public async Task Nfs_Store_Exercises_Real_Export_File_And_Directory_Operations()
    {
        string host;
        if (Fixture.RunsInsideServiceNetwork)
        {
            host = Fixture.NfsHost;
        }
        else if (await Fixture.InitializeNfsAsync())
        {
            host = Fixture.DirectHost;
        }
        else
        {
            try
            {
                await Fixture.RunInsideServiceNetworkAsync(
                    "IntegrationTests.Storage.StorageExtensionsProviderTests.1.1.Nfs_Store_Exercises_Real_Export_File_And_Directory_Operations.1.1.0",
                    "nfs");
            }
            catch (Exception ex) when (IsNfsKernelUnavailable(ex))
            {
                TestContext.Current!.Execution.OverrideResult(
                    TestState.Skipped,
                    "NFS integration requires the host NFS kernel module and privileged container support.");
            }

            return;
        }

        var root = $"nfs-{Guid.NewGuid():N}";
        using var store = NfsStorage.FromExport(host, "/export", 0, 0, root);

        store.ShouldBeOfType<NfsStore>();
        (await store.IsFileSystem()).ShouldBeTrue();
        (await store.IsSeekable()).ShouldBeTrue();

        await store.CreateDirectory("", force: false);
        await store.CreateDirectory("folder/child", force: true);
        await Should.ThrowAsync<IOException>(() => store.CreateDirectory("folder/child", force: false));
        await store.CreateDirectory("folder/child", force: true);

        await store.SetObject("folder/child/data.txt", new MemoryStream("abcdefghij"u8.ToArray()), null, append: false);
        (await store.ObjectExists("folder/child/data.txt")).ShouldBeTrue();
        (await store.DirectoryExists("folder/child")).ShouldBeTrue();
        (await store.DirectoryExists("missing")).ShouldBeFalse();
        (await store.GetObjectInfo("folder/child/data.txt"))!.Size.ShouldBe(10);
        (await store.GetObjectInfo("missing")).ShouldBeNull();
        (await store.GetObjectLength("folder/child/data.txt")).ShouldBe(10);
        (await store.GetObjectLength("missing", defaultValue: 17)).ShouldBe(17);

        await using (var range = await store.OpenRange("folder/child/data.txt", 3, 4))
        using (var reader = new StreamReader(range))
        {
            (await reader.ReadToEndAsync()).ShouldBe("defg");
        }

        await store.SetObject("folder/child/data.txt", new MemoryStream("!"u8.ToArray()), null, append: true);
        (await store.GetText("folder/child/data.txt")).ShouldBe("abcdefghij!");

        var children = await store.ListObjects(new StorageListOptions { FolderPath = "folder", Recurse = false });
        children.ShouldContain(item => item.Name == "child" && item.IsFolder);

        (await store.MoveObject("folder/child/data.txt", "folder/child/moved.txt", overwrite: false)).ShouldBeTrue();
        (await store.MoveObject("folder/child/moved.txt", "folder/child/moved.txt", overwrite: false)).ShouldBeFalse();
        (await store.MoveObject("missing", "folder/child/absent.txt", overwrite: true)).ShouldBeFalse();
        await store.MoveDirectory("folder/child", "folder/renamed");
        (await store.ObjectExists("folder/renamed/moved.txt")).ShouldBeTrue();
        await store.DeleteDirectory("folder", recursive: true);
        (await store.ObjectExists("folder/renamed/moved.txt")).ShouldBeFalse();
        await store.DeleteObject("missing");

        using var unrooted = NfsStorage.FromExport(host, "/export", 0, 0);
        await Should.ThrowAsync<InvalidOperationException>(() => unrooted.DeleteDirectory("/", recursive: false));
    }

    [Test]
    public async Task Smb_Store_Exercises_Real_Share_File_And_Directory_Operations()
    {
        string host;
        if (Fixture.RunsInsideServiceNetwork)
        {
            host = Fixture.SmbHost;
        }
        else if (await Fixture.InitializeSmbAsync())
        {
            host = Fixture.DirectHost;
        }
        else
        {
            await Fixture.RunInsideServiceNetworkAsync(
                "IntegrationTests.Storage.StorageExtensionsProviderTests.1.1.Smb_Store_Exercises_Real_Share_File_And_Directory_Operations.1.1.0",
                "smb");
            return;
        }

        var root = $"smb-{Guid.NewGuid():N}";
        using var setup = SmbStorage.FromCredentials(host, "storage", "test", "test", "WORKGROUP");
        await setup.CreateDirectory(root, force: false);
        using var store = SmbStorage.FromCredentials(host, "storage", "test", "test", "WORKGROUP", root);

        store.ShouldBeOfType<SmbStore>();
        (await store.IsFileSystem()).ShouldBeTrue();
        (await store.IsSeekable()).ShouldBeTrue();

        await store.CreateDirectory("", force: false);
        await store.CreateDirectory("folder/child", force: true);
        await store.CreateDirectory("folder/child", force: false);
        await store.CreateDirectory("folder/child", force: true);
        await store.SetObject("folder/child/data.txt", new MemoryStream("abcdefghij"u8.ToArray()), null, append: false);

        (await store.ObjectExists("folder/child/data.txt")).ShouldBeTrue();
        (await store.ObjectExists("")).ShouldBeTrue();
        (await store.DirectoryExists("folder/child")).ShouldBeTrue();
        (await store.DirectoryExists("missing")).ShouldBeFalse();
        (await store.GetObjectInfo("folder/child/data.txt"))!.Size.ShouldBe(10);
        (await store.GetObjectInfo("missing")).ShouldBeNull();
        (await store.GetObjectLength("folder/child/data.txt")).ShouldBe(10);
        (await store.GetObjectLength("missing", defaultValue: 17)).ShouldBe(17);

        await using (var range = await store.OpenRange("folder/child/data.txt", 3, 4))
        using (var reader = new StreamReader(range))
        {
            (await reader.ReadToEndAsync()).ShouldBe("defg");
        }

        await store.SetObject("folder/child/data.txt", new MemoryStream("!"u8.ToArray()), null, append: true);
        (await store.GetText("folder/child/data.txt")).ShouldBe("abcdefghij!");
        (await store.OpenWrite("folder/child/data.txt", overwrite: false)).ShouldBeNull();

        var children = await store.ListObjects(new StorageListOptions { FolderPath = "folder", Recurse = false });
        children.ShouldContain(item => item.Name == "child" && item.IsFolder);

        (await store.MoveObject("folder/child/data.txt", "folder/child/moved.txt", overwrite: false)).ShouldBeTrue();
        (await store.MoveObject("folder/child/moved.txt", "folder/child/moved.txt", overwrite: false)).ShouldBeFalse();
        (await store.MoveObject("missing", "folder/child/absent.txt", overwrite: true)).ShouldBeFalse();
        await store.MoveDirectory("folder/child", "folder/renamed");
        (await store.ObjectExists("folder/renamed/moved.txt")).ShouldBeTrue();
        await store.DeleteDirectory("folder", recursive: true);
        (await store.ObjectExists("folder/renamed/moved.txt")).ShouldBeFalse();
        await store.DeleteObject("missing");
        await Should.ThrowAsync<InvalidOperationException>(() => setup.DeleteDirectory("", recursive: false));
        await setup.DeleteDirectory(root, recursive: true);
    }

    private static bool IsNfsKernelUnavailable(Exception exception)
        => exception.ToString().Contains("kernel module nfs is missing", StringComparison.OrdinalIgnoreCase)
            || exception.ToString().Contains("nfs module is not loaded", StringComparison.OrdinalIgnoreCase)
            || exception.ToString().Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);
}

internal sealed class StorageProtocolContainerFixture : IAsyncDisposable
{
    private readonly string _nfsDataPath = Path.Combine(Path.GetTempPath(), $"froststream-nfs-{Guid.NewGuid():N}");
    private readonly IContainer? _smb;
    private readonly IContainer? _nfs;
    private bool _smbInitialized;
    private bool _nfsInitialized;

    public bool RunsInsideServiceNetwork { get; } =
        string.Equals(Environment.GetEnvironmentVariable("FROSTSTREAM_STORAGE_SERVICE_NETWORK"), "1", StringComparison.Ordinal);
    public string DirectHost => "127.0.0.1";
    public string SmbHost => RunsInsideServiceNetwork ? "storage-smb" : DirectHost;
    public string NfsHost => RunsInsideServiceNetwork ? "storage-nfs" : DirectHost;

    public StorageProtocolContainerFixture()
    {
        Directory.CreateDirectory(_nfsDataPath);
        if (RunsInsideServiceNetwork)
        {
            return;
        }

        _smb = CreateSmbContainer().WithPortBinding(445, 445).Build();
        _nfs = CreateNfsContainer()
            .WithPortBinding(2049, 2049)
            .WithPortBinding(111, 111)
            .WithPortBinding(32767, 32767)
            .WithPortBinding(32765, 32765)
            .Build();
    }

    private ContainerBuilder CreateSmbContainer()
        => new ContainerBuilder("dperson/samba:latest")
            .WithEnvironment("USER", "test;test")
            .WithEnvironment("SHARE", "storage;/share;yes;no;no;test")
            .WithCommand("-p")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(445));

    private ContainerBuilder CreateNfsContainer()
        => new ContainerBuilder("erichough/nfs-server:2.2.1")
            .WithPrivileged(true)
            .WithEnvironment("NFS_VERSION", "3")
            .WithEnvironment("NFS_EXPORT_0", "/export *(rw,no_subtree_check,no_root_squash,insecure)")
            .WithEnvironment("NFS_PORT", "2049")
            .WithEnvironment("NFS_PORT_MOUNTD", "32767")
            .WithEnvironment("NFS_PORT_STATD_IN", "32765")
            .WithBindMount(_nfsDataPath, "/export")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(2049));

    public async Task<bool> InitializeSmbAsync()
    {
        if (_smbInitialized)
        {
            return true;
        }

        try
        {
            await _smb!.StartAsync();
            _smbInitialized = true;
            return true;
        }
        catch (Exception ex) when (ex.ToString().Contains("rootlessport cannot expose privileged port 445", StringComparison.OrdinalIgnoreCase))
        {
            await _smb!.DisposeAsync();
            return false;
        }
    }

    public async Task<bool> InitializeNfsAsync()
    {
        if (_nfsInitialized)
        {
            return true;
        }

        try
        {
            await _nfs!.StartAsync();
            _nfsInitialized = true;
            return true;
        }
        catch (Exception ex) when (ex.ToString().Contains("rootlessport cannot expose privileged port 111", StringComparison.OrdinalIgnoreCase))
        {
            await _nfs!.DisposeAsync();
            return false;
        }
        catch (Exception ex) when (ex.ToString().Contains("kernel module nfs is missing", StringComparison.OrdinalIgnoreCase)
                                   || ex.ToString().Contains("nfs module is not loaded", StringComparison.OrdinalIgnoreCase))
        {
            await _nfs!.DisposeAsync();
            return false;
        }
    }

    public async Task RunInsideServiceNetworkAsync(string testUid, string protocol)
    {
        var network = new NetworkBuilder().Build();
        await using (network)
        {
            var smb = CreateSmbContainer()
                .WithNetwork(network)
                .WithNetworkAliases("storage-smb")
                .Build();
            var nfs = CreateNfsContainer()
                .WithNetwork(network)
                .WithNetworkAliases("storage-nfs")
                .Build();

            await using (smb)
            await using (nfs)
            {
                if (protocol == "smb")
                {
                    await smb.StartAsync();
                }
                else
                {
                    await nfs.StartAsync();
                }

                var outputDirectory = Path.GetDirectoryName(typeof(StorageExtensionsProviderTests).Assembly.Location)!;
                var reportRoot = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? "TestResults";
                var reportDirectory = Path.GetFullPath(Path.Combine(reportRoot, "storage-containerized", protocol));
                Directory.CreateDirectory(reportDirectory);

                var runner = new ContainerBuilder("mcr.microsoft.com/dotnet/aspnet:11.0")
                    .WithNetwork(network)
                    .WithBindMount(outputDirectory, "/tests")
                    .WithBindMount(reportDirectory, "/reports")
                    .WithWorkingDirectory("/tests")
                    .WithEnvironment("FROSTSTREAM_STORAGE_SERVICE_NETWORK", "1")
                    .WithEntrypoint("sleep")
                    .WithCommand("infinity")
                    .Build();

                await using (runner)
                {
                    await runner.StartAsync();
                    var result = await runner.ExecAsync(
                    [
                        "dotnet", "IntegrationTests.dll",
                        "--filter-uid", testUid,
                        "--results-directory", "/reports",
                        "--coverage",
                        "--coverage-output", $"coverage.{protocol}.cobertura.xml",
                        "--coverage-output-format", "cobertura"
                    ]);

                    result.ExitCode.ShouldBe(0, $"Containerized {protocol} test failed.\n{result.Stdout}\n{result.Stderr}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var containers = new[] { _smb, _nfs }.Where(container => container is not null)
            .Select(container => container!.DisposeAsync().AsTask());
        await Task.WhenAll(containers);
        if (Directory.Exists(_nfsDataPath))
        {
            Directory.Delete(_nfsDataPath, recursive: true);
        }
    }
}
