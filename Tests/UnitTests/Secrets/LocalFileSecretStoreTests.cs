using System.Text;
using System.Text.Json;
using DataBridge.Data;
using DataBridge.Lite;
using FrostStream.Lite.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Application;
using Shared.Auth;
using Shared.Database;
using Shared.Secrets;
using Shared.Storage;
using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Secrets;

public sealed class LocalFileSecretStoreTests
{
    [Test]
    public async Task Ciphertext_RoundTrips_Across_Restart_Without_Exposing_Plaintext()
    {
        using var installation = new TemporaryInstallation();
        await using (var first = installation.CreateStore())
        {
            await first.Store.WriteAsync("storage/archive", new Dictionary<string, string>
            {
                ["username"] = "synthetic-user",
                ["password"] = "synthetic-secret-value"
            });
        }

        var payload = await File.ReadAllBytesAsync(installation.SecretFile("storage/archive"));
        Encoding.UTF8.GetString(payload).ShouldNotContain("synthetic-secret-value");

        await using var restarted = installation.CreateStore();
        var values = await restarted.Store.ReadAsync("storage/archive");
        values!["username"].ShouldBe("synthetic-user");
        values["password"].ShouldBe("synthetic-secret-value");

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(installation.SecretFile("storage/archive"))
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(installation.SecretsPath).ShouldBe(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(installation.KeysPath).ShouldBe(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var directory in Directory.EnumerateDirectories(
                         installation.SecretsPath, "*", SearchOption.AllDirectories))
            {
                File.GetUnixFileMode(directory).ShouldBe(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (var keyFile in Directory.EnumerateFiles(installation.KeysPath))
            {
                File.GetUnixFileMode(keyFile).ShouldBe(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Test]
    public async Task Overwrite_And_Delete_Are_Complete_And_Idempotent()
    {
        using var installation = new TemporaryInstallation();
        await using var instance = installation.CreateStore();
        await instance.Store.WriteAsync("storage/archive", new Dictionary<string, string> { ["value"] = "old" });
        await instance.Store.WriteAsync("storage/archive", new Dictionary<string, string> { ["value"] = "new" });
        (await instance.Store.ReadAsync("storage/archive"))!["value"].ShouldBe("new");

        await instance.Store.DeleteAsync("storage/archive");
        await instance.Store.DeleteAsync("storage/archive");
        (await instance.Store.ReadAsync("storage/archive")).ShouldBeNull();
    }

    [Test]
    public async Task Concurrent_Writes_Never_Produce_A_Partial_Document()
    {
        using var installation = new TemporaryInstallation();
        await using var instance = installation.CreateStore();
        var writes = Enumerable.Range(0, 32).Select(index => instance.Store.WriteAsync(
            "cookies/users/single-user-owner/youtube",
            new Dictionary<string, string> { ["content"] = $"complete-{index:D2}" }));
        await Task.WhenAll(writes);

        var result = await instance.Store.ReadAsync("cookies/users/single-user-owner/youtube");
        result!["content"].ShouldStartWith("complete-");
        Directory.EnumerateFiles(installation.SecretsPath, "*.tmp", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Test]
    public async Task Invalid_And_Oversized_Paths_And_Fields_Are_Rejected()
    {
        using var installation = new TemporaryInstallation();
        await using var instance = installation.CreateStore();
        await Should.ThrowAsync<ArgumentException>(() => instance.Store.WriteAsync(
            "../escape", new Dictionary<string, string> { ["value"] = "x" }));
        await Should.ThrowAsync<ArgumentException>(() => instance.Store.WriteAsync(
            "/absolute", new Dictionary<string, string> { ["value"] = "x" }));
        await Should.ThrowAsync<ArgumentException>(() => instance.Store.WriteAsync(
            "storage/bad key", new Dictionary<string, string> { ["value"] = "x" }));
        await Should.ThrowAsync<ArgumentException>(() => instance.Store.WriteAsync(
            "storage/good", new Dictionary<string, string> { ["bad/key"] = "x" }));
    }

    [Test]
    public async Task Secret_Path_Cannot_Traverse_A_Symbolic_Link()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var installation = new TemporaryInstallation();
        await using var instance = installation.CreateStore();
        var outside = Path.Combine(installation.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(installation.SecretsPath, "linked"), outside);

        await Should.ThrowAsync<InvalidOperationException>(() => instance.Store.WriteAsync(
            "linked/escape", new Dictionary<string, string> { ["value"] = "must-not-escape" }));
        File.Exists(Path.Combine(outside, "escape.secret")).ShouldBeFalse();
    }

    [Test]
    public async Task Interrupted_Temporary_File_Is_Ignored()
    {
        using var installation = new TemporaryInstallation();
        await using var instance = installation.CreateStore();
        var directory = Path.Combine(installation.SecretsPath, "storage");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, ".archive.secret.interrupted.tmp"), "partial plaintext");

        (await instance.Store.ReadAsync("storage/archive")).ShouldBeNull();
        await instance.Store.ValidateExistingSecretsAsync();
    }

    [Test]
    public async Task Mismatched_Master_Key_Refuses_Read_And_Overwrite_With_Recovery_Instructions()
    {
        using var installation = new TemporaryInstallation();
        var unrelatedKeys = Path.Combine(installation.Root, "unrelated-keys");
        await using (var seedUnrelatedKey = installation.CreateStore(unrelatedKeys))
        {
        }
        await using (var original = installation.CreateStore())
            await original.Store.WriteAsync("storage/archive", new Dictionary<string, string> { ["password"] = "recover-me" });

        await using var withoutKeys = installation.CreateStore(unrelatedKeys);
        var readError = await Should.ThrowAsync<LocalSecretRecoveryException>(() =>
            withoutKeys.Store.ReadAsync("storage/archive"));
        readError.Message.ShouldContain("Restore the matching");
        readError.Message.ShouldNotContain("recover-me");

        await Should.ThrowAsync<LocalSecretRecoveryException>(() => withoutKeys.Store.WriteAsync(
            "storage/archive", new Dictionary<string, string> { ["password"] = "replacement" }));
        await Should.ThrowAsync<LocalSecretRecoveryException>(() =>
            withoutKeys.Store.DeleteAsync("storage/archive"));
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(installation.SecretFile("storage/archive")))
            .ShouldNotContain("replacement");
    }

    [Test]
    public async Task Missing_Master_Key_Is_Detected_Before_A_Replacement_Is_Created()
    {
        using var installation = new TemporaryInstallation();
        await using (var original = installation.CreateStore())
            await original.Store.WriteAsync("storage/archive", new Dictionary<string, string> { ["password"] = "recover-me" });

        File.Delete(installation.KeyFile);
        var error = Should.Throw<LocalSecretRecoveryException>(() => installation.CreateStore());
        error.Message.ShouldContain("Restore the matching Lite NSec master key");
        File.Exists(installation.KeyFile).ShouldBeFalse();
    }

    [Test]
    public void Malformed_Master_Key_Produces_A_Redacted_Recovery_Error()
    {
        using var installation = new TemporaryInstallation();
        Directory.CreateDirectory(installation.KeysPath);
        File.WriteAllText(installation.KeyFile, "not-an-nsec-key");

        var error = Should.Throw<LocalSecretRecoveryException>(() => installation.CreateStore());
        error.Message.ShouldNotContain(installation.KeyFile);
        error.Message.ShouldContain("Restore the matching Lite NSec master key");
    }

    [Test]
    public async Task Malformed_Ciphertext_Produces_The_Same_Redacted_Recovery_Error()
    {
        using var installation = new TemporaryInstallation();
        await using (var initialized = installation.CreateStore())
        {
        }
        var file = installation.SecretFile("storage/archive");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "not-a-protected-payload");
        await using var instance = installation.CreateStore();

        var error = await Should.ThrowAsync<LocalSecretRecoveryException>(() =>
            instance.Store.ValidateExistingSecretsAsync());
        error.Message.ShouldNotContain(file);
        error.Message.ShouldContain("do not re-enter or overwrite credentials");
    }

    [Test]
    public async Task Ciphertext_Is_Bound_To_Its_Logical_Path()
    {
        using var installation = new TemporaryInstallation();
        await using var instance = installation.CreateStore();
        await instance.Store.WriteAsync("storage/source", new Dictionary<string, string> { ["value"] = "bound" });
        var destination = installation.SecretFile("storage/destination");
        File.Copy(installation.SecretFile("storage/source"), destination);

        await Should.ThrowAsync<LocalSecretRecoveryException>(() =>
            instance.Store.ReadAsync("storage/destination"));
    }

    [Test]
    public void Backup_Manifest_Requires_Ciphertext_And_Master_Key_As_A_Pair()
    {
        using var installation = new TemporaryInstallation();
        var provider = new LocalSecretBackupManifestProvider(Options.Create(installation.Options()));
        var manifest = provider.GetManifest();
        manifest.SecretsDirectory.ShouldBe(installation.SecretsPath);
        manifest.KeyDirectory.ShouldBe(installation.KeysPath);
        manifest.KeyFileName.ShouldBe(LocalFileSecretStoreOptions.KeyFileName);
        manifest.Algorithm.ShouldBe(LocalFileSecretStoreOptions.AlgorithmName);
        manifest.FormatVersion.ShouldBe(2);
    }

    [Test]
    public async Task Credentialed_Storage_Config_Hydrates_From_Encrypted_Local_Store()
    {
        using var installation = new TemporaryInstallation();
        await using var secretInstance = installation.CreateStore();
        await secretInstance.Store.WriteAsync(SecretPaths.ForStorage("archive"), new Dictionary<string, string>
        {
            [StorageSecretSplitter.S3AccessKeyId] = "synthetic-access",
            [StorageSecretSplitter.S3SecretKeyId] = "synthetic-secret"
        });
        await using var services = BuildDatabase(secretInstance.Store);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
            var entity = new StorageConfigEntity { Key = "archive", Description = "test" };
            entity.ApplyStoredParameters(new S3CompatibleObjectStorageStored
            {
                Provider = S3CompatibleObjectStorageProvider.MinIo,
                BucketName = "archive",
                Endpoint = "http://127.0.0.1:9000",
                ForcePathStyle = true
            });
            db.StorageConfigs.Add(entity);
            await db.SaveChangesAsync();
        }

        var client = new LocalStorageConfigClient(
            services.GetRequiredService<IServiceScopeFactory>(), secretInstance.Store);
        var result = await client.GetStorageConfigAsync("archive");
        result.Found.ShouldBeTrue();
        var parameters = result.Parameters.ShouldNotBeNull();
        parameters.ShouldContain("synthetic-access");
        parameters.ShouldContain("synthetic-secret");

        using var provider = new CachingStoreProvider(client);
        var credentialedStore = await provider.GetAsync("archive");
        credentialedStore.ShouldNotBeNull();
    }

    [Test]
    public async Task Cookie_Profile_Writes_Encrypted_Content_And_Never_Returns_It()
    {
        using var installation = new TemporaryInstallation();
        await using var secretInstance = installation.CreateStore();
        await using var services = BuildDatabase(secretInstance.Store);
        await using var scope = services.CreateAsyncScope();
        var controller = new CookiesController(
            secretInstance.Store,
            new CookieProfileApplication(scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>(), SystemClock.Instance),
            new FixedCurrentOwner(),
            NullLogger<CookiesController>.Instance);

        var result = await controller.Upsert("youtube", new CookieUpsertRequest
        {
            Content = "synthetic-cookie-body",
            Site = "youtube.test",
            DisplayName = "Synthetic"
        }, CancellationToken.None);

        var response = result.ShouldBeOfType<OkObjectResult>();
        JsonSerializer.Serialize(response.Value).ShouldNotContain("synthetic-cookie-body");
        var stored = await secretInstance.Store.ReadAsync(
            SecretPaths.ForUserCookieProfile(AuthConstants.SingleUserSubject, "youtube"));
        stored!["content"].ShouldBe("synthetic-cookie-body");
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(installation.SecretFile(
                SecretPaths.ForUserCookieProfile(AuthConstants.SingleUserSubject, "youtube"))))
            .ShouldNotContain("synthetic-cookie-body");
    }

    [Test]
    public async Task Cookie_Materialization_Reads_Local_Ciphertext_And_Cleans_Up_Plaintext()
    {
        using var installation = new TemporaryInstallation();
        await using var secretInstance = installation.CreateStore();
        var logicalPath = SecretPaths.ForUserCookieProfile(AuthConstants.SingleUserSubject, "youtube");
        await secretInstance.Store.WriteAsync(logicalPath, new Dictionary<string, string>
        {
            ["content"] = "# Netscape HTTP Cookie File\n.example.test\tTRUE"
        });
        var scratch = Path.Combine(installation.Root, "scratch");

        var materializer = await CookieMaterializer.CreateFromPathAsync(
            secretInstance.Store,
            logicalPath,
            scratch,
            NullLogger.Instance);
        var materializedPath = materializer.FilePath.ShouldNotBeNull();
        (await File.ReadAllTextAsync(materializedPath)).ShouldContain("Netscape HTTP Cookie File");

        await materializer.DisposeAsync();
        File.Exists(materializedPath).ShouldBeFalse();
    }

    private static ServiceProvider BuildDatabase(ISecretStore secretStore)
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString("N");
        services.AddLogging();
        services.AddSingleton(secretStore);
        services.AddDbContext<DataBridgeDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private sealed class TemporaryInstallation : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"froststream-phase9-{Guid.NewGuid():N}");
        public string SecretsPath => Path.Combine(Root, "secrets");
        public string KeysPath => Path.Combine(Root, "keys");
        public string KeyFile => Path.Combine(KeysPath, LocalFileSecretStoreOptions.KeyFileName);

        public StoreLifetime CreateStore(string? keysPath = null)
        {
            keysPath ??= KeysPath;
            var store = new LocalFileSecretStore(
                Microsoft.Extensions.Options.Options.Create(Options(keysPath)));
            return new StoreLifetime(store);
        }

        public LocalFileSecretStoreOptions Options(string? keysPath = null) => new()
        {
            SecretsPath = SecretsPath,
            KeyDirectoryPath = keysPath ?? KeysPath
        };

        public string SecretFile(string logicalPath)
        {
            var segments = logicalPath.Split('/');
            return Path.Combine([SecretsPath, .. segments[..^1], $"{segments[^1]}.secret"]);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class StoreLifetime(LocalFileSecretStore store)
        : IAsyncDisposable
    {
        public LocalFileSecretStore Store { get; } = store;

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
