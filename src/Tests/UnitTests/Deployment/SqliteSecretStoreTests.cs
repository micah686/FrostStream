using System.Security.Cryptography;
using System.Text;
using FrostStream.ApplicationContracts;
using NSubstitute;
using Shared.Messaging;
using Shared.Storage;
using DataBridge.Persistence;
using DataBridge.Persistence.Secrets;
using DataBridge.Persistence.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shared.Secrets;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class SqliteSecretStoreTests
{
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Factory { get; }
        public LocalSecretStoreOptions Options { get; }
        public Fixture()
        {
            Factory = new(new(PersistenceProvider.Sqlite, Path.Combine(DirectoryPath, "test.db"), 5));
            Options = new(Path.Combine(DirectoryPath, "keys"));
            new SqliteSchemaInitializer(Factory, SystemClock.Instance).Initialize();
        }
        public SqliteSecretStore Store() => new(Factory, Options);
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    [Test]
    public async Task Secrets_Are_Encrypted_And_Survive_Restart_Replacement_And_Deletion()
    {
        using var f = new Fixture();
        var path = SecretPaths.ForStorage("test");
        var values = new Dictionary<string, string> { ["password"] = "secret-value-東京", ["empty"] = "", ["Case"] = "one", ["case"] = "two" };
        await f.Store().WriteAsync(path, values);
        using (var connection = f.Factory.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload FROM local_secrets WHERE path=$path";
            command.Parameters.AddWithValue("$path", path);
            var ciphertext = (byte[])command.ExecuteScalar()!;
            Encoding.UTF8.GetString(ciphertext).ShouldNotContain(values["password"]);
            Encoding.UTF8.GetString(ciphertext).ShouldNotContain("password");
        }
        Directory.GetFiles(f.Options.KeyRingPath, "key-*.xml").ShouldNotBeEmpty();
        if (!OperatingSystem.IsWindows())
            (File.GetUnixFileMode(f.Options.KeyRingPath) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)).ShouldBe((UnixFileMode)0);
        var restarted = f.Store();
        (await restarted.ReadAsync(path)).ShouldBe(values);
        await restarted.WriteAsync(path, new Dictionary<string, string> { ["replacement"] = "new-value" });
        var replaced = (await restarted.ReadAsync(path)).ShouldNotBeNull();
        replaced.Count.ShouldBe(1);
        replaced["replacement"].ShouldBe("new-value");
        await restarted.WriteAsync("empty-document", new Dictionary<string, string>());
        (await restarted.ReadAsync("empty-document")).ShouldNotBeNull().ShouldBeEmpty();
        await restarted.DeleteAsync(path);
        await restarted.DeleteAsync(path);
        (await restarted.ReadAsync(path)).ShouldBeNull();
        (await restarted.ReadAsync("missing")).ShouldBeNull();
    }

    [Test]
    public async Task Key_Rotation_And_Relocating_Database_With_Companion_Keys_Preserve_Secrets()
    {
        using var f = new Fixture();
        await f.Store().WriteAsync("before-rotation", new Dictionary<string, string> { ["password"] = "original" });
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName(LocalSecretStoreOptions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(f.Options.KeyRingPath));
        using (var provider = services.BuildServiceProvider())
            provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        var rotated = f.Store();
        await rotated.WriteAsync("after-rotation", new Dictionary<string, string> { ["password"] = "rotated" });
        (await rotated.ReadAsync("before-rotation")).ShouldNotBeNull()["password"].ShouldBe("original");
        var movedKeys = Path.Combine(f.DirectoryPath, "restored-keys");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(movedKeys);
        else Directory.CreateDirectory(movedKeys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.GetFiles(f.Options.KeyRingPath)) File.Copy(file, Path.Combine(movedKeys, Path.GetFileName(file)));
        var movedFactory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, Path.Combine(f.DirectoryPath, "restored.db"), 5));
        using (var source = f.Factory.OpenConnection())
        using (var target = movedFactory.OpenConnection()) source.BackupDatabase(target);
        var restored = new SqliteSecretStore(movedFactory, new(movedKeys));
        (await restored.ReadAsync("before-rotation")).ShouldNotBeNull()["password"].ShouldBe("original");
        (await restored.ReadAsync("after-rotation")).ShouldNotBeNull()["password"].ShouldBe("rotated");
    }

    [Test]
    public async Task Tampering_Path_Swaps_And_Missing_Keys_Fail_Without_Returning_Plaintext()
    {
        using var f = new Fixture();
        var store = f.Store();
        await store.WriteAsync("first", new Dictionary<string, string> { ["password"] = "secret" });
        await store.WriteAsync("second", new Dictionary<string, string> { ["password"] = "other" });
        var wrongKeys = new SqliteSecretStore(f.Factory, new(Path.Combine(f.DirectoryPath, "wrong-keys")));
        await Should.ThrowAsync<CryptographicException>(() => wrongKeys.ReadAsync("first"));
        using var connection = f.Factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE local_secrets SET payload=(SELECT payload FROM local_secrets WHERE path='first') WHERE path='second'";
        command.ExecuteNonQuery();
        await Should.ThrowAsync<CryptographicException>(() => store.ReadAsync("second"));
        command.CommandText = "SELECT payload FROM local_secrets WHERE path='first'";
        var ciphertext = (byte[])command.ExecuteScalar()!;
        ciphertext[^1] ^= 1;
        command.CommandText = "UPDATE local_secrets SET payload=$payload WHERE path='first'";
        command.Parameters.AddWithValue("$payload", ciphertext);
        command.ExecuteNonQuery();
        await Should.ThrowAsync<CryptographicException>(() => store.ReadAsync("first"));
    }

    [Test]
    public async Task Shared_Storage_Client_Hydrates_Encrypted_Credentials_After_Restart()
    {
        using var f = new Fixture();
        await f.Store().WriteAsync(SecretPaths.ForStorage("remote"), new Dictionary<string, string>
            { [StorageSecretSplitter.NetworkPassword] = "remote-password" });
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<StorageGetRequestMessage, StorageOperationResponseMessage>(StorageSubjects.GetStorage,
            Arg.Any<StorageGetRequestMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new StorageOperationResponseMessage
            {
                Success = true,
                Entity = UnitTests.Storage.StorageTestHelpers.CreateDto(StorageMethod.Network, "remote")
            });
        var response = await new NatsStorageConfigClient(bus, f.Store()).GetStorageConfigAsync("remote");
        response.Found.ShouldBeTrue();
        response.Parameters.ShouldNotBeNull().ShouldContain("remote-password");
    }

    [Test]
    public void Key_Directory_Options_Are_Resolved_And_Invalid_Locations_Are_Rejected()
    {
        using var f = new Fixture();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        var persistence = new PersistenceOptions(PersistenceProvider.Sqlite, Path.Combine(f.DirectoryPath, "test.db"), 5);
        LocalSecretStoreOptions.FromConfiguration(configuration, persistence, f.DirectoryPath)
            .KeyRingPath.ShouldBe(persistence.SqlitePath + ".keys");
        configuration["Secrets:Local:KeyRingPath"] = "custom-keys";
        LocalSecretStoreOptions.FromConfiguration(configuration, persistence, f.DirectoryPath)
            .KeyRingPath.ShouldBe(Path.Combine(f.DirectoryPath, "custom-keys"));
        foreach (var invalid in new[] { " ", persistence.SqlitePath, Path.GetPathRoot(f.DirectoryPath)! })
        {
            configuration["Secrets:Local:KeyRingPath"] = invalid;
            Should.Throw<InvalidOperationException>(() => LocalSecretStoreOptions.FromConfiguration(configuration, persistence, f.DirectoryPath));
        }
        if (!OperatingSystem.IsWindows())
        {
            var sharedDirectory = Path.Combine(f.DirectoryPath, "shared-keys");
            Directory.CreateDirectory(sharedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
            Should.Throw<InvalidOperationException>(() => new SqliteSecretStore(f.Factory, new(sharedDirectory)));
        }
    }

    [Test]
    public async Task Concurrent_Writes_Are_Atomic_And_Cancelled_Operations_Preserve_Existing_Values()
    {
        using var f = new Fixture();
        var store = f.Store();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => store.WriteAsync("shared",
            new Dictionary<string, string> { ["one"] = i.ToString(), ["two"] = i.ToString() }))));
        var values = (await store.ReadAsync("shared")).ShouldNotBeNull();
        values["one"].ShouldBe(values["two"]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => store.WriteAsync("shared", new Dictionary<string, string>(), cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => store.ReadAsync("shared", cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => store.DeleteAsync("shared", cancelled.Token));
        (await store.ReadAsync("shared")).ShouldBe(values);
        await Should.ThrowAsync<ArgumentException>(() => store.WriteAsync(" ", values));
    }
}
