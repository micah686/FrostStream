using DataBridge;
using DataBridge.Data;
using DataBridge.Persistence;
using DataBridge.Persistence.Postgres;
using DataBridge.Persistence.Schema;
using DataBridge.Persistence.Sqlite;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using static UnitTests.DataBridge.CoreRepositoryPersistenceTests;

namespace UnitTests.DataBridge;

public sealed class PersistenceUpgradeTests
{
    private static readonly Instant Now = Instant.FromUtc(2026,10,3,12,0);
    private sealed class LegacyPostgresInitializer(IMigrationRunner runner) : IApplicationSchemaInitializer
    { public void Initialize(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); runner.MigrateUp(97); } }
    private static DownloadRequested Request() => new() { JobId=Guid.NewGuid(), CorrelationId=Guid.NewGuid(), SourceUrl="https://test", StorageKey="default", RequestedBy="alice", MessageId=Guid.NewGuid(), OperationKey="upgrade", OccurredAt=Now };

    [Test]
    public async Task Upgrade_From_Released_Sqlite_V1_And_Postgres_M097_Preserves_Application_And_Workflow_Data()
    {
        foreach (var postgres in string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES")) ? new[] {false} : new[] {false,true})
        {
            await using var f = await Fixture.Create(postgres,builder =>
            {
                if (postgres) builder.Services.AddScoped<IApplicationSchemaInitializer,LegacyPostgresInitializer>();
                else builder.Services.AddScoped<IApplicationSchemaInitializer>(sp => new SqliteSchemaInitializer(sp.GetRequiredService<SqliteConnectionFactory>(),sp.GetRequiredService<IClock>(),[]));
            });
            var request=Request(); var repo=new DownloadFlowV2Repository(f.Db,new FixedClock(Now),NullDownloadJobStateNotifier.Instance);
            var run=(await repo.CreateInitialRunAsync(request,true)).ShouldNotBeNull();
            var store=await WorkflowPersistenceTests.Store(f);
            var type=await store.TypeStore.InsertOrGetStoredType(new("upgrade-workflow"));
            var id=new Cleipnir.ResilientFunctions.Storage.StoredId(type,Cleipnir.ResilientFunctions.Storage.StoredInstance.Create("instance"));
            await store.CreateFunction(id,new("instance"),[1,2,3],0,0,0,null);
            await f.Db.Database.ExecuteSqlRawAsync(postgres ? "UPDATE scheduling.scheduled_tasks SET enabled=false WHERE key='channel-scan-refresh'" : "UPDATE scheduling_scheduled_tasks SET enabled=0 WHERE key='channel-scan-refresh'");
            using var scope=f.ScopeFactory.CreateScope();
            if (postgres) scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            else new SqliteSchemaInitializer(scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>(),new FixedClock(Now)).Initialize();
            if (postgres) scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            else new SqliteSchemaInitializer(scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>(),new FixedClock(Now)).Initialize();
            (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).CurrentRunId.ShouldBe(run.RunId);
            (await f.Db.DownloadJobHistory.CountAsync()).ShouldBe(1);
            (await (await WorkflowPersistenceTests.Store(f)).GetFunction(id))!.Parameter.ShouldBe([1,2,3]);
            (await repo.ReconcileForStartupAsync()).GenerationStartedAt.ShouldBe(Now+Duration.FromTicks(10));
            await using var connection=await f.Database.OpenConnectionAsync();
            await using var command=connection.CreateCommand();
            command.CommandText=postgres ? "SELECT MAX(\"Version\") FROM \"VersionInfo\"" : "SELECT MAX(version) FROM froststream_schema_versions";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(postgres ? 98L : 4L);
            command.CommandText=postgres ? "SELECT enabled FROM scheduling.scheduled_tasks WHERE key='channel-scan-refresh'" : "SELECT enabled FROM scheduling_scheduled_tasks WHERE key='channel-scan-refresh'";
            Convert.ToBoolean(await command.ExecuteScalarAsync()).ShouldBeFalse();
        }
    }

    [Test]
    public async Task Sqlite_Rebuild_Preserves_Data_Children_Checks_Indexes_And_Independent_Workflow_History()
    {
        await using var f=await Fixture.Create(false);
        await using var connection=(SqliteConnection)await f.Database.OpenConnectionAsync();
        using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO jobs_download_startup_state VALUES('download',123);
            CREATE TABLE rebuild_child (name TEXT REFERENCES jobs_download_startup_state(name) ON DELETE CASCADE);
            INSERT INTO rebuild_child VALUES('download');
            """; command.ExecuteNonQuery();
        var sql=SqliteSchemaMigrations.Rebuild("jobs_download_startup_state",["name","generation_started_at"],
            "CREATE TABLE jobs_download_startup_state_upgrade (name TEXT NOT NULL PRIMARY KEY,generation_started_at INTEGER NOT NULL CHECK(generation_started_at >= 0),note TEXT);",
            ["CREATE INDEX startup_generation_test ON jobs_download_startup_state(generation_started_at);"]);
        var upgrade=new SqliteSchemaMigration(5,"test reviewed rebuild",sql,true);
        SqliteSchemaMigrations.Apply(connection,[..SqliteSchemaMigrations.All,upgrade],default);
        SqliteSchemaMigrations.Apply(connection,[..SqliteSchemaMigrations.All,upgrade],default);
        command.CommandText="SELECT generation_started_at FROM jobs_download_startup_state WHERE name='download'"; command.ExecuteScalar().ShouldBe(123L);
        command.CommandText="SELECT count(*) FROM rebuild_child"; command.ExecuteScalar().ShouldBe(1L);
        command.CommandText="SELECT count(*) FROM sqlite_master WHERE name='startup_generation_test'"; command.ExecuteScalar().ShouldBe(1L);
        command.CommandText="SELECT version FROM cleipnir_schema"; command.ExecuteScalar().ShouldBe(1L);
        command.CommandText="PRAGMA foreign_keys"; command.ExecuteScalar().ShouldBe(1L);
        command.CommandText="PRAGMA foreign_key_check"; command.ExecuteScalar().ShouldBeNull();
        command.CommandText="UPDATE jobs_download_startup_state SET generation_started_at=-1"; Should.Throw<SqliteException>(()=>command.ExecuteNonQuery());
        await Should.ThrowAsync<InvalidOperationException>(()=>Task.Run(()=>SqliteSchemaMigrations.Apply(connection,SqliteSchemaMigrations.All,default)));
    }

    [Test]
    public async Task Failed_Or_Cancelled_Upgrade_Rolls_Back_Ddl_Data_And_History_And_Can_Retry()
    {
        await using var f=await Fixture.Create(false);
        await using var connection=(SqliteConnection)await f.Database.OpenConnectionAsync();
        using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO jobs_download_startup_state VALUES('download',123)"; command.ExecuteNonQuery();
        var bad=new SqliteSchemaMigration(5,"failed rebuild",SqliteSchemaMigrations.Rebuild("jobs_download_startup_state",["name","generation_started_at"],
            "CREATE TABLE jobs_download_startup_state_upgrade (name TEXT NOT NULL PRIMARY KEY,generation_started_at INTEGER NOT NULL CHECK(generation_started_at > 200));",[]),true);
        Should.Throw<SqliteException>(()=>SqliteSchemaMigrations.Apply(connection,[..SqliteSchemaMigrations.All,bad],default));
        command.CommandText="SELECT generation_started_at FROM jobs_download_startup_state"; command.ExecuteScalar().ShouldBe(123L);
        command.CommandText="SELECT MAX(version) FROM froststream_schema_versions"; command.ExecuteScalar().ShouldBe(4L);
        command.CommandText="SELECT count(*) FROM sqlite_master WHERE name='jobs_download_startup_state_upgrade'"; command.ExecuteScalar().ShouldBe(0L);
        using var cancelled=new CancellationTokenSource();
        connection.CreateFunction("cancel_upgrade",()=> { cancelled.Cancel(); return 0; });
        var cancel=new SqliteSchemaMigration(5,"cancelled upgrade","ALTER TABLE jobs_download_startup_state ADD COLUMN note TEXT; SELECT cancel_upgrade();");
        Should.Throw<OperationCanceledException>(()=>SqliteSchemaMigrations.Apply(connection,[..SqliteSchemaMigrations.All,cancel],cancelled.Token));
        command.CommandText="SELECT count(*) FROM pragma_table_info('jobs_download_startup_state') WHERE name='note'"; command.ExecuteScalar().ShouldBe(0L);
        command.CommandText="PRAGMA foreign_keys"; command.ExecuteScalar().ShouldBe(1L);
        var valid=new SqliteSchemaMigration(5,"retry upgrade","ALTER TABLE jobs_download_startup_state ADD COLUMN note TEXT;");
        SqliteSchemaMigrations.Apply(connection,[..SqliteSchemaMigrations.All,valid],default);
        command.CommandText="SELECT MAX(version) FROM froststream_schema_versions"; command.ExecuteScalar().ShouldBe(5L);
    }

    [Test]
    public async Task Upgrade_Rejects_Changed_Or_Gapped_History_And_Concurrent_Initializers_Agree()
    {
        await using var f=await Fixture.Create(false); using var scope=f.ScopeFactory.CreateScope();
        var factory=scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
        await Task.WhenAll(Enumerable.Range(0,6).Select(_=>Task.Run(()=>new SqliteSchemaInitializer(factory,new FixedClock(Now)).Initialize())));
        using var connection=factory.OpenConnection(); using var command=connection.CreateCommand();
        command.CommandText="SELECT count(*) FROM froststream_schema_versions"; command.ExecuteScalar().ShouldBe(4L);
        command.CommandText="UPDATE froststream_schema_versions SET checksum='changed' WHERE version=2"; command.ExecuteNonQuery();
        Should.Throw<InvalidOperationException>(()=>new SqliteSchemaInitializer(factory,new FixedClock(Now)).Initialize());
        command.CommandText="UPDATE froststream_schema_versions SET checksum=$checksum,version=5 WHERE version=2";
        command.Parameters.AddWithValue("$checksum",SqliteSchemaMigrations.All[0].Checksum); command.ExecuteNonQuery();
        Should.Throw<InvalidOperationException>(()=>new SqliteSchemaInitializer(factory,new FixedClock(Now)).Initialize());
    }
}
