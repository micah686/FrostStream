using DataBridge.Persistence;
using DataBridge.Persistence.Secrets;
using DataBridge.Persistence.Workflows;
using DataBridge.Messaging;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Storage;
using FrostStream.ApplicationContracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using DataBridge.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class SqliteBackupServiceClientTests
{
    [Test]
    public async Task Creates_Online_Full_Snapshot_And_Verifies_It()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "frostreamlitedb");
            var backupDirectory = Path.Combine(root, "backups");
            var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, database, 5));
            new SqliteSchemaInitializer(factory, NodaTime.SystemClock.Instance).Initialize();
            using (var connection = factory.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE sample (value TEXT NOT NULL); INSERT INTO sample VALUES ('before');";
                command.ExecuteNonQuery();
            }

            var client = CreateClient(factory, root, backupDirectory);
            var created = await client.CreateAsync(new("test", "full"));

            created.Status.ShouldBe("completed");
            created.Label.ShouldNotBeNull();
            File.Exists(Path.Combine(backupDirectory, created.Label)).ShouldBeTrue();
            using (var connection = factory.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO sample VALUES ('after');";
                command.ExecuteNonQuery();
            }

            (await client.VerifyAsync(new(created.Label))).Status.ShouldBe("completed");
            var listed = await client.ListBackupsAsync();
            listed.RepositoryOk.ShouldBeTrue();
            listed.Backups.Count.ShouldBe(1);
            listed.Backups[0].Label.ShouldBe(created.Label);
            listed.Backups[0].Type.ShouldBe("full");
            (await client.ListJobsAsync()).Count.ShouldBe(2);
            (await client.GetJobAsync(created.JobId)).ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Rejects_Unsupported_Backup_Operations_And_Removes_Incomplete_Files()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, Path.Combine(root, "db"), 5));
            using (factory.OpenConnection()) { }
            var client = CreateClient(factory, root, Path.Combine(root, "backups"));

            await Should.ThrowAsync<NotSupportedException>(() => client.CreateAsync(new(null, "diff")));
            await Should.ThrowAsync<NotSupportedException>(() => client.VerifyAsync(new(Deep: true)));
            (await client.ListBackupsAsync()).Backups.ShouldBeEmpty();
            Directory.EnumerateFiles(Path.Combine(root, "backups")).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Offline_File_Replacement_Recovers_Rotated_Secrets_Workflows_Queues_And_Staged_Objects_With_Rollback()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "frostreamlitedb");
        var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, database, 5));
        var options = new LocalSecretStoreOptions(Path.Combine(root, "custom-secret-keys"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            new SqliteSchemaInitializer(factory, NodaTime.SystemClock.Instance).Initialize();
            await new SqliteSecretStore(factory, options).WriteAsync("older-secret", new Dictionary<string, string> { ["value"] = "before-rotation" });
            var services = new ServiceCollection();
            services.AddDataProtection().SetApplicationName(LocalSecretStoreOptions.ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(options.KeyRingPath));
            using (var provider = services.BuildServiceProvider())
                provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            await new SqliteSecretStore(factory, options).WriteAsync("newer-secret", new Dictionary<string, string> { ["value"] = "after-rotation" });
            Directory.GetFiles(options.KeyRingPath, "key-*.xml").Length.ShouldBeGreaterThanOrEqualTo(2);
            // The whole directory, including metadata unrelated to the current active key, is retained.
            File.WriteAllText(Path.Combine(options.KeyRingPath, "ring-note.txt"), "retain me");
            var objects = new SqliteStagedObjectStore(factory, "imports");
            await objects.PutAsync("manifest", new MemoryStream([1, 2, 3]));
            var workflows = new SqliteFunctionStore(factory);
            await workflows.Initialize();
            var type = await workflows.TypeStore.InsertOrGetStoredType(new("recovery-test"));
            var id = new StoredId(type, StoredInstance.Create("persisted"));
            await workflows.CreateFunction(id, new("persisted"), Encoding.UTF8.GetBytes("workflow-state"), 10, null, 1, null);
            var queue = new SqliteDurableTransport(factory, NullLogger<SqliteDurableTransport>.Instance);
            await queue.PublishAsync("recovery.work", 77, "persisted-message");
            var client = CreateClient(factory, root, Path.Combine(root, "backups"), options);
            var job = await client.CreateAsync(new("recovery", "full"));
            var snapshot = Path.Combine(root, "backups", job.Label!);
            (await client.VerifyAsync(new(job.Label))).Status.ShouldBe("completed");
            (await client.ListBackupsAsync()).Backups.Single().KeyRingBackupPresent.ShouldBe(true);
            (await client.ListBackupsAsync()).KeyRingPath.ShouldBe(options.KeyRingPath);
            Directory.GetFiles(snapshot + ".keys").Select(Path.GetFileName).Order()
                .ShouldBe(Directory.GetFiles(options.KeyRingPath).Select(Path.GetFileName).Order());
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(snapshot + ".keys").ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var file in Directory.GetFiles(snapshot + ".keys"))
                    File.GetUnixFileMode(file).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await new SqliteSecretStore(factory, options).WriteAsync("newer-secret", new Dictionary<string, string> { ["value"] = "rollback-value" });
            await objects.PutAsync("manifest", new MemoryStream([9]));
            // All database handles are closed (pooling is disabled). Preserve live files before replacement.
            var rollback = Path.Combine(root, "rollback");
            SqliteRecoveryArtifacts.CreatePrivateDirectory(rollback);
            var preserved = Path.Combine(rollback, "database");
            File.Move(database, preserved);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(database + suffix)) File.Move(database + suffix, preserved + suffix);
            var preservedKeys = Path.Combine(rollback, "keys");
            Directory.Move(options.KeyRingPath, preservedKeys);
            var rollbackHash = SHA256.HashData(File.ReadAllBytes(preserved));
            File.Copy(snapshot, database);
            SqliteRecoveryArtifacts.CopyKeys(snapshot + ".keys", options.KeyRingPath, timeout.Token);
            await SqliteRecoveryArtifacts.ValidateSnapshotAsync(database, options.KeyRingPath, timeout.Token);
            var restarted = new SqliteSecretStore(factory, options);
            (await restarted.ReadAsync("older-secret")).ShouldNotBeNull()["value"].ShouldBe("before-rotation");
            (await restarted.ReadAsync("newer-secret")).ShouldNotBeNull()["value"].ShouldBe("after-rotation");
            var recoveredFlows = new SqliteFunctionStore(factory);
            await recoveredFlows.Initialize();
            (await recoveredFlows.GetFunction(id)).ShouldNotBeNull().Parameter.ShouldBe(Encoding.UTF8.GetBytes("workflow-state"));
            using var manifest = new MemoryStream();
            await new SqliteStagedObjectStore(factory, "imports").GetAsync("manifest", manifest);
            manifest.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var consumeStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var consuming = new SqliteDurableTransport(factory, NullLogger<SqliteDurableTransport>.Instance)
                .ConsumeAsync<int>(StreamName.From("recovery"), SubjectName.From("recovery.work"), async context =>
                {
                    await context.AckAsync();
                    delivered.TrySetResult(context.Message);
                }, cancellationToken: consumeStop.Token);
            (await delivered.Task.WaitAsync(timeout.Token)).ShouldBe(77);
            consumeStop.Cancel();
            try { await consuming; } catch (OperationCanceledException) { }

            // Recovery on another installation uses the copied ring, never the source provider's cache.
            var relocated = Path.Combine(root, "another-installation");
            Directory.CreateDirectory(relocated);
            var relocatedDatabase = Path.Combine(relocated, "frostreamlitedb");
            var relocatedKeys = Path.Combine(relocated, "keys");
            File.Copy(snapshot, relocatedDatabase);
            SqliteRecoveryArtifacts.CopyKeys(snapshot + ".keys", relocatedKeys, timeout.Token);
            var relocatedStore = new SqliteSecretStore(new(new(PersistenceProvider.Sqlite, relocatedDatabase, 5)), new(relocatedKeys));
            (await relocatedStore.ReadAsync("older-secret")).ShouldNotBeNull()["value"].ShouldBe("before-rotation");
            (await relocatedStore.ReadAsync("newer-secret")).ShouldNotBeNull()["value"].ShouldBe("after-rotation");

            // A failed secret-recovery check preserves the original database and complete ring for rollback.
            Directory.Move(options.KeyRingPath, Path.Combine(root, "failed-recovery-keys"));
            SqliteRecoveryArtifacts.CreatePrivateDirectory(options.KeyRingPath);
            var failure = await Should.ThrowAsync<InvalidDataException>(() => SqliteRecoveryArtifacts.ValidateSnapshotAsync(database, options.KeyRingPath, timeout.Token));
            failure.Message.ShouldContain("Preserve the current database and key ring");
            SHA256.HashData(File.ReadAllBytes(preserved)).ShouldBe(rollbackHash);
            File.Move(database, Path.Combine(root, "failed-recovery-database"));
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(database + suffix)) File.Move(database + suffix, Path.Combine(root, "failed-recovery-database") + suffix);
            Directory.Delete(options.KeyRingPath);
            File.Copy(preserved, database);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(preserved + suffix)) File.Copy(preserved + suffix, database + suffix);
            SqliteRecoveryArtifacts.CopyKeys(preservedKeys, options.KeyRingPath, timeout.Token);
            (await new SqliteSecretStore(factory, options).ReadAsync("newer-secret")).ShouldNotBeNull()["value"].ShouldBe("rollback-value");
            File.Exists(preserved).ShouldBeTrue();
            Directory.Exists(preservedKeys).ShouldBeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task Missing_Or_Changed_Companion_Files_And_Legacy_Database_Only_Backups_Fail_Recovery_Verification()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, Path.Combine(root, "db"), 5));
        var options = new LocalSecretStoreOptions(Path.Combine(root, "keys"));
        try
        {
            new SqliteSchemaInitializer(factory, NodaTime.SystemClock.Instance).Initialize();
            await new SqliteSecretStore(factory, options).WriteAsync("secret", new Dictionary<string, string> { ["value"] = "original" });
            File.WriteAllText(Path.Combine(options.KeyRingPath, "older-key-metadata"), "keep all files");
            var client = CreateClient(factory, root, Path.Combine(root, "backups"), options);
            var job = await client.CreateAsync(new(null, "full"));
            var snapshot = Path.Combine(root, "backups", job.Label!);
            File.Delete(Path.Combine(snapshot + ".keys", "older-key-metadata"));
            await Should.ThrowAsync<InvalidDataException>(() => client.VerifyAsync(new(job.Label)));
            File.Copy(Path.Combine(options.KeyRingPath, "older-key-metadata"), Path.Combine(snapshot + ".keys", "older-key-metadata"));
            await File.AppendAllTextAsync(Directory.GetFiles(snapshot + ".keys", "key-*.xml").Single(), "changed");
            await Should.ThrowAsync<InvalidDataException>(() => client.VerifyAsync(new(job.Label)));
            Directory.Delete(snapshot + ".keys", true);
            File.Delete(snapshot + ".recovery.json");
            var listed = (await client.ListBackupsAsync()).Backups.Single();
            listed.KeyRingBackupPresent.ShouldBe(false);
            listed.HasError.ShouldBeTrue();
            var missing = await Should.ThrowAsync<InvalidDataException>(() => client.VerifyAsync(new(job.Label)));
            missing.Message.ShouldContain("companion key backup or recovery manifest is missing");
            (await new SqliteSecretStore(factory, options).ReadAsync("secret")).ShouldNotBeNull()["value"].ShouldBe("original");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task Unrecoverable_Secrets_Prevent_Publication_And_Clean_Staged_Artifacts_Without_Changing_Live_Data()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, Path.Combine(root, "db"), 5));
        var options = new LocalSecretStoreOptions(Path.Combine(root, "keys"));
        try
        {
            new SqliteSchemaInitializer(factory, NodaTime.SystemClock.Instance).Initialize();
            await new SqliteSecretStore(factory, options).WriteAsync("secret", new Dictionary<string, string> { ["value"] = "preserved" });
            var preservedKeys = Path.Combine(root, "preserved-keys");
            Directory.Move(options.KeyRingPath, preservedKeys);
            var client = CreateClient(factory, root, Path.Combine(root, "backups"), options);
            await Should.ThrowAsync<InvalidDataException>(() => client.CreateAsync(new(null, "full")));
            Directory.EnumerateFileSystemEntries(Path.Combine(root, "backups")).ShouldBeEmpty();
            File.Exists(Path.Combine(root, "db")).ShouldBeTrue();
            (await new SqliteSecretStore(factory, new(preservedKeys)).ReadAsync("secret")).ShouldNotBeNull()["value"].ShouldBe("preserved");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static SqliteBackupServiceClient CreateClient(SqliteConnectionFactory factory, string root, string backupDirectory, LocalSecretStoreOptions? secrets = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Directory"] = backupDirectory
        }).Build();
        return new(factory, configuration, new TestHostEnvironment(root), secrets ?? new(Path.Combine(root, "keys")));
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
