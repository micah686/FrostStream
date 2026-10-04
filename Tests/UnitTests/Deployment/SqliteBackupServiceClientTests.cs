using DataBridge.Persistence;
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

    private static SqliteBackupServiceClient CreateClient(SqliteConnectionFactory factory, string root, string backupDirectory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Directory"] = backupDirectory
        }).Build();
        return new(factory, configuration, new TestHostEnvironment(root));
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
