using System.Diagnostics;
using System.Text.Json;
using DataBridge;
using DataBridge.Data;
using DataBridge.Persistence;
using DataBridge.Persistence.Sqlite;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;
using Shared.Database;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class SqlitePersistenceFoundationTests
{
    [Test]
    public void Selection_Requires_Explicit_Lite_OptIn_And_Validates_File_And_Busy_Budget()
    {
        var configuration = new ConfigurationManager();
        var root = Path.GetTempPath();
        PersistenceOptions.FromConfiguration(configuration, root).Provider.ShouldBe(PersistenceProvider.Postgres);
        configuration["Deployment:Mode"] = "Lite";
        PersistenceOptions.FromConfiguration(configuration, root).Provider.ShouldBe(PersistenceProvider.Postgres);
        configuration["Persistence:Sqlite:Enabled"] = "true";
        PersistenceOptions.FromConfiguration(configuration, root).SqlitePath.ShouldBe(PersistenceOptions.DefaultSqlitePath);
        configuration["Persistence:Sqlite:Path"] = "dev/core.sqlite";
        PersistenceOptions.FromConfiguration(configuration, root).SqlitePath.ShouldBe(Path.Combine(root, "dev/core.sqlite"));
        configuration["Deployment:Mode"] = "Full";
        Should.Throw<InvalidOperationException>(() => PersistenceOptions.FromConfiguration(configuration, root));
        configuration["Deployment:Mode"] = "Lite";
        configuration["Persistence:Sqlite:Path"] = ":memory:";
        Should.Throw<InvalidOperationException>(() => PersistenceOptions.FromConfiguration(configuration, root));
        configuration["Persistence:Sqlite:Path"] = "db.sqlite";
        foreach (var seconds in new[] { "0", "-1", "61" })
        {
            configuration["Persistence:Sqlite:BusyTimeoutSeconds"] = seconds;
            Should.Throw<InvalidOperationException>(() => PersistenceOptions.FromConfiguration(configuration, root));
        }
    }

    [Test]
    public void Initialization_Only_Registers_Selected_Database_And_Runtime_Can_Select_Sqlite()
    {
        using var fixture = new DatabaseFixture();
        fixture.Host.Services.GetService<IMigrationRunner>().ShouldBeNull();
        fixture.Host.Services.GetService<NpgsqlDataSource>().ShouldBeNull();
        fixture.Host.Services.GetServices<IHostedService>().ShouldBeEmpty();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Deployment:Mode"] = "Lite";
        builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
        // Persistence is ready; the ordinary Lite host still requires Phase 3 transport adapters.
        Should.Throw<NotSupportedException>(() => builder.AddDataBridgeModule()).Message.ShouldContain("phase 3");
        builder.Services.ShouldNotContain(d => d.ServiceType == typeof(NpgsqlDataSource));
    }

    [Test]
    public void Fresh_Initialization_Is_Repeatable_And_Seeds_Are_Installed_Once()
    {
        using var fixture = new DatabaseFixture();
        fixture.Host.InitializeDataBridge();
        fixture.Host.InitializeDataBridge();
        using var connection = fixture.Factory.OpenConnection();
        Scalar(connection, $"SELECT count(*) FROM {SqliteBaseline.HistoryTable}").ShouldBe(2L);
        Scalar(connection, $"SELECT MAX(version) FROM {SqliteBaseline.HistoryTable}").ShouldBe(2L);
        Scalar(connection, "SELECT count(*) FROM scheduling_scheduled_tasks").ShouldBe(10L);
        Scalar(connection, "SELECT count(*) FROM storage_storage_keys").ShouldBe(1L);
        Scalar(connection, "SELECT count(*) FROM storage_storage_keys_local").ShouldBe(1L);
        Scalar(connection, "SELECT next_due_at FROM scheduling_scheduled_tasks WHERE key='channel-scan-refresh'")
            .ShouldBe(SqliteStorageEncoding.ToUnixMicroseconds(DataBridgeTestHelpers.Now + Duration.FromMinutes(30)));
        // User edits survive repeated initialization; initialization is not an upsert/seed reset.
        Execute(connection, "UPDATE scheduling_scheduled_tasks SET enabled=0 WHERE key='channel-scan-refresh'");
        fixture.Host.InitializeDataBridge();
        Scalar(connection, "SELECT enabled FROM scheduling_scheduled_tasks WHERE key='channel-scan-refresh'").ShouldBe(0L);
        Scalar(connection, "PRAGMA integrity_check").ShouldBe("ok");
        Scalar(connection, "PRAGMA foreign_key_check").ShouldBeNull();
        Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='VersionInfo'").ShouldBe(0L);
        Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'cleipnir_%'").ShouldBe(9L);
        Scalar(connection, "SELECT version FROM cleipnir_schema WHERE id=1").ShouldBe(1L);
    }

    [Test]
    public void Baseline_Contains_All_Catalog_Columns_Keys_Indexes_And_Foreign_Key_Actions()
    {
        using var fixture = new DatabaseFixture();
        fixture.Host.InitializeDataBridge();
        using var connection = fixture.Factory.OpenConnection();
        using var manifest = JsonDocument.Parse(SqliteBaseline.ManifestJson);
        var tables = manifest.RootElement.GetProperty("tables").EnumerateArray().ToArray();
        tables.Length.ShouldBe(82);
        Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'cleipnir_%'")
            .ShouldBe(84L); // 82 baseline tables, the v2 startup boundary and separate SQLite history.
        foreach (var table in tables)
        {
            var schema = table.GetProperty("schema").GetString()!;
            var name = SqliteStorageEncoding.TableName(schema, table.GetProperty("name").GetString()!);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{name}\")";
            var columns = new Dictionary<string, (string Type, bool NotNull, int Key)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    columns.Add(reader.GetString(1), (reader.GetString(2), reader.GetInt32(3) == 1, reader.GetInt32(5)));
            var expectedColumns = table.GetProperty("columns").EnumerateArray().ToArray();
            columns.Count.ShouldBe(expectedColumns.Length, name);
            foreach (var column in expectedColumns)
            {
                var colName = column.GetProperty("name").GetString()!;
                var actual = columns[colName];
                actual.NotNull.ShouldBe(!column.GetProperty("nullable").GetBoolean(), $"{name}.{colName}");
                var type = column.GetProperty("type").GetString()!;
                var expectedType = type switch
                {
                    "bigint" or "integer" or "boolean" or "timestamp with time zone" or "timestamp without time zone" => "INTEGER",
                    "double precision" => "REAL",
                    _ => "TEXT"
                };
                actual.Type.ShouldBe(expectedType, $"{name}.{colName}");
            }
            foreach (var keyColumn in table.GetProperty("constraints").EnumerateArray()
                         .Single(constraint => constraint.GetProperty("kind").GetString() == "p")
                         .GetProperty("columns").EnumerateArray())
                columns[keyColumn.GetString()!].Key.ShouldBeGreaterThan(0, name);

            command.CommandText = $"PRAGMA index_list(\"{name}\")";
            var indexes = new Dictionary<string, (bool Unique, bool Partial)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) indexes.Add(reader.GetString(1), (reader.GetBoolean(2), reader.GetBoolean(4)));
            foreach (var index in table.GetProperty("indexes").EnumerateArray())
            {
                if (index.GetProperty("primary").GetBoolean()) continue;
                var indexName = SqliteStorageEncoding.TableName(schema, index.GetProperty("name").GetString()!);
                indexes.ShouldContainKey(indexName, name);
                indexes[indexName].Unique.ShouldBe(index.GetProperty("unique").GetBoolean(), indexName);
                indexes[indexName].Partial.ShouldBe(index.GetProperty("predicate").ValueKind != JsonValueKind.Null, indexName);
            }
            command.CommandText = $"PRAGMA foreign_key_list(\"{name}\")";
            var foreignKeys = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) foreignKeys.Add($"{reader.GetString(2)}:{reader.GetString(3)}:{reader.GetString(4)}:{reader.GetString(5)}:{reader.GetString(6)}");
            var actions = new Dictionary<string, string> { ["a"] = "NO ACTION", ["r"] = "RESTRICT", ["c"] = "CASCADE", ["n"] = "SET NULL", ["d"] = "SET DEFAULT" };
            var expectedForeignKeys = new List<string>();
            foreach (var key in table.GetProperty("constraints").EnumerateArray().Where(key => key.GetProperty("kind").GetString() == "f"))
            {
                var target = SqliteStorageEncoding.TableName(key.GetProperty("targetSchema").GetString()!, key.GetProperty("targetTable").GetString()!);
                var from = key.GetProperty("columns").EnumerateArray().ToArray();
                var to = key.GetProperty("targetColumns").EnumerateArray().ToArray();
                for (var i = 0; i < from.Length; i++)
                    expectedForeignKeys.Add($"{target}:{from[i].GetString()}:{to[i].GetString()}:{actions[key.GetProperty("onUpdate").GetString()!]}:{actions[key.GetProperty("onDelete").GetString()!]}");
            }
            foreignKeys.Order(StringComparer.Ordinal).ShouldBe(expectedForeignKeys.Order(StringComparer.Ordinal), name);
        }
    }

    [Test]
    public async Task Every_Raw_And_Reopened_Ef_Connection_Enforces_Foreign_Keys_And_Wal()
    {
        using var fixture = new DatabaseFixture();
        fixture.Host.InitializeDataBridge();
        foreach (var asyncOpen in new[] { false, true })
        {
            await using var connection = asyncOpen ? await fixture.Factory.OpenConnectionAsync() : fixture.Factory.OpenConnection();
            AssertConnectionSetup(connection);
            Should.Throw<SqliteException>(() => Execute(connection,
                "INSERT INTO storage_storage_keys_local(storage_key_id, protocol, path) VALUES(99999, 'local', '/tmp')"))
                .SqliteExtendedErrorCode.ShouldBe(787);
        }
        using var scope = fixture.Host.Services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await db.Database.OpenConnectionAsync();
        var efConnection = (SqliteConnection)db.Database.GetDbConnection();
        AssertConnectionSetup(efConnection);
        Execute(efConnection, "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=0");
        await db.Database.CloseConnectionAsync();
        db.Database.OpenConnection();
        AssertConnectionSetup(efConnection);
        Should.Throw<SqliteException>(() => Execute(efConnection,
            "INSERT INTO storage_storage_keys_local(storage_key_id, protocol, path) VALUES(99999, 'local', '/tmp')"))
            .SqliteExtendedErrorCode.ShouldBe(787);
    }

    [Test]
    public void Baseline_Enforces_Enum_Json_Key_Unique_And_Composite_Cascade_Constraints()
    {
        using var fixture = new DatabaseFixture();
        fixture.Host.InitializeDataBridge();
        using var connection = fixture.Factory.OpenConnection();
        foreach (var sql in new[]
        {
            "INSERT INTO storage_storage_keys(key,method) VALUES('default','Local')",
            "INSERT INTO storage_storage_keys(key,method) VALUES('UPPER','Local')",
            "UPDATE storage_storage_keys_local SET protocol='unknown'",
            "INSERT INTO downloads_download_option_presets(key,name,ytdlp_options_json) VALUES('json-test','Test','invalid JSON')",
            "INSERT INTO scheduling_scheduled_tasks(key,task_type,cron,interval_seconds,timezone,enabled,catchup_policy) VALUES('test-task','test','*',5,'UTC',1,'Coalesce')",
            "INSERT INTO auth_access_policy_assignments(policy_id,principal_type,principal_id) VALUES('00000000000000000000000000000000','invalid','user')"
        }) Should.Throw<SqliteException>(() => Execute(connection, sql)).SqliteErrorCode.ShouldBe(19, sql);
        const string guid = "1234567890abcdef1234567890abcdef";
        Execute(connection, $"INSERT INTO media_media(media_guid) VALUES('{guid}')");
        Execute(connection, $"INSERT INTO media_media_content_id_versions(media_guid,version_num,content_hash_xxh128,storage_key,storage_path) VALUES('{guid}',1,'cid','default','/test')");
        Execute(connection, $"INSERT INTO media_audio_renditions(rendition_id,media_guid,source_version_num,status,storage_key) VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','{guid}',1,'pending','default')");
        Execute(connection, $"DELETE FROM media_media WHERE media_guid='{guid}'");
        Scalar(connection, "SELECT count(*) FROM media_audio_renditions").ShouldBe(0L);
        Scalar(connection, "SELECT count(*) FROM media_media_content_id_versions").ShouldBe(0L);
    }

    [Test]
    public async Task Provider_Models_Coexist_And_Sqlite_Mappings_Match_The_Baseline()
    {
        using var fixture = new DatabaseFixture();
        fixture.Host.InitializeDataBridge();
        using var sqliteScope = fixture.Host.Services.CreateScope();
        await using var sqlite = sqliteScope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Deployment:Mode"] = "Full";
        builder.Configuration["Persistence:Sqlite:Enabled"] = "false";
        builder.Configuration["ConnectionStrings:froststreamdb"] = "Host=localhost;Database=phase2b_model_only;Username=postgres;Password=unused";
        builder.AddDataBridgePersistence();
        using var postgresHost = builder.Build();
        using var postgresScope = postgresHost.Services.CreateScope();
        using var postgres = postgresScope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        postgres.Database.ProviderName.ShouldBe("Npgsql.EntityFrameworkCore.PostgreSQL");
        postgresHost.Services.GetRequiredService<IMigrationRunner>().ShouldNotBeNull();
        postgres.Model.FindEntityType(typeof(DownloadJobEntity))!.GetSchema().ShouldBe("jobs");
        postgres.Model.FindEntityType(typeof(DownloadJobEntity))!.FindProperty(nameof(DownloadJobEntity.Status))!
            .GetColumnType().ShouldBe("jobs.download_job_status");
        postgres.Model.FindEntityType(typeof(MediaEntity))!.FindProperty(nameof(MediaEntity.CreatedAt))!
            .GetDefaultValueSql().ShouldBe("CURRENT_TIMESTAMP");
        using var manifest = JsonDocument.Parse(SqliteBaseline.ManifestJson);
        var tables = manifest.RootElement.GetProperty("tables").EnumerateArray().ToDictionary(
            table => SqliteStorageEncoding.TableName(table.GetProperty("schema").GetString()!, table.GetProperty("name").GetString()!));
        foreach (var entity in sqlite.Model.GetEntityTypes())
        {
            entity.GetSchema().ShouldBeNull();
            var table = tables[entity.GetTableName()!];
            var columns = table.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()).ToArray();
            foreach (var property in entity.GetProperties()) columns.ShouldContain(property.GetColumnName(), entity.Name);
        }
        (await sqlite.StorageLocalConfigs.SingleAsync()).Protocol.ShouldBe(Shared.Storage.LocalStorageProtocol.Local);
        var guid = Guid.NewGuid();
        var media = new MediaEntity { MediaGuid = guid };
        sqlite.Media.Add(media);
        var instant = Instant.FromUnixTimeTicks(-12345670);
        sqlite.Entry(media).Property(nameof(MediaEntity.CreatedAt)).CurrentValue = instant;
        await sqlite.SaveChangesAsync();
        sqlite.ChangeTracker.Clear();
        (await sqlite.Media.SingleAsync()).CreatedAt.ShouldBe(instant);
        using var connection = fixture.Factory.OpenConnection();
        Scalar(connection, "SELECT media_guid FROM media_media").ShouldBe(guid.ToString("N"));
        Scalar(connection, "SELECT created_at FROM media_media").ShouldBe(-1234567L);
    }

    [Test]
    public void Unknown_Or_Altered_History_And_Unversioned_Databases_Are_Rejected()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Factory.OpenConnection();
        Execute(connection, "CREATE TABLE user_data(value TEXT)");
        Should.Throw<InvalidOperationException>(() => fixture.Host.InitializeDataBridge()).Message.ShouldContain("unversioned");
        Execute(connection, "DROP TABLE user_data");
        fixture.Host.InitializeDataBridge();
        Execute(connection, $"UPDATE {SqliteBaseline.HistoryTable} SET checksum='changed'");
        Should.Throw<InvalidOperationException>(() => fixture.Host.InitializeDataBridge()).Message.ShouldContain("checksum");
        Execute(connection, $"UPDATE {SqliteBaseline.HistoryTable} SET version=99 WHERE version=1");
        Should.Throw<InvalidOperationException>(() => fixture.Host.InitializeDataBridge()).Message.ShouldContain("newer");
        Scalar(connection, "SELECT count(*) FROM storage_storage_keys").ShouldBe(1L);
    }

    [Test]
    public void Failed_Migration_Rolls_Back_All_New_Ddl_And_Can_Be_Retried()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Factory.OpenConnection();
        Execute(connection, $"CREATE TABLE {SqliteBaseline.HistoryTable}(version INTEGER PRIMARY KEY, checksum TEXT NOT NULL, applied_at INTEGER NOT NULL)");
        // Collide late in the baseline to prove earlier table creation rolls back with it.
        Execute(connection, "CREATE TABLE storage_storage_keys(value TEXT)");
        Should.Throw<SqliteException>(() => fixture.Host.InitializeDataBridge());
        Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").ShouldBe(2L);
        Scalar(connection, $"SELECT count(*) FROM {SqliteBaseline.HistoryTable}").ShouldBe(0L);
        Execute(connection, "DROP TABLE storage_storage_keys");
        fixture.Host.InitializeDataBridge();
        Scalar(connection, "SELECT count(*) FROM storage_storage_keys").ShouldBe(1L);
    }

    [Test]
    public async Task Concurrent_Initialization_Is_Serialized_And_Write_Contention_Has_A_Finite_Budget()
    {
        using var fixture = new DatabaseFixture(busyTimeoutSeconds: 1);
        await Task.WhenAll(Task.Run(() => fixture.Host.InitializeDataBridge()), Task.Run(() => fixture.Host.InitializeDataBridge()));
        using var writer = fixture.Factory.OpenConnection();
        using var contender = fixture.Factory.OpenConnection();
        using var transaction = writer.BeginTransaction(deferred: false);
        var elapsed = Stopwatch.StartNew();
        Should.Throw<SqliteException>(() => Execute(contender, "UPDATE storage_storage_keys SET description='busy' WHERE id=1"))
            .SqliteErrorCode.ShouldBe(5);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
        transaction.Rollback();
        Execute(contender, "UPDATE storage_storage_keys SET description='available' WHERE id=1");
        Scalar(contender, "SELECT description FROM storage_storage_keys WHERE id=1").ShouldBe("available");
    }

    [Test]
    public void PreCanceled_Initialization_Does_Not_Create_A_Database()
    {
        using var fixture = new DatabaseFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Should.Throw<OperationCanceledException>(() => fixture.Host.InitializeDataBridge(cancellation.Token));
        File.Exists(fixture.Options.SqlitePath).ShouldBeFalse();
    }

    private static void AssertConnectionSetup(SqliteConnection connection)
    {
        Scalar(connection, "PRAGMA foreign_keys").ShouldBe(1L);
        Scalar(connection, "PRAGMA journal_mode").ShouldBe("wal");
        Scalar(connection, "PRAGMA busy_timeout").ShouldBe(100L);
        connection.DefaultTimeout.ShouldBeGreaterThan(0);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "froststream-sqlite-tests", Guid.NewGuid().ToString("N"));
        public IHost Host { get; }
        public PersistenceOptions Options => Host.Services.GetRequiredService<PersistenceOptions>();
        public SqliteConnectionFactory Factory => Host.Services.GetRequiredService<SqliteConnectionFactory>();

        public DatabaseFixture(int busyTimeoutSeconds = 5)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["Deployment:Mode"] = "Lite";
            builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
            builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "nested", "core.sqlite");
            builder.Configuration["Persistence:Sqlite:BusyTimeoutSeconds"] = busyTimeoutSeconds.ToString();
            builder.Logging.ClearProviders();
            builder.AddDataBridgePersistence();
            builder.Services.AddSingleton<IClock>(new FixedClock(DataBridgeTestHelpers.Now));
            Host = builder.Build();
        }

        public void Dispose()
        {
            Host.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
