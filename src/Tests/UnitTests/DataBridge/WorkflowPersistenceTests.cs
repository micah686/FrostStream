using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Cleipnir.ResilientFunctions;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Messaging;
using Cleipnir.ResilientFunctions.PostgreSQL;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Flows;
using DataBridge.Persistence;
using DataBridge.Persistence.Sqlite;
using DataBridge.Persistence.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cleipnir.ResilientFunctions.CoreRuntime.Invocation;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Shared.Messaging;
using Shared.Database;
using Cleipnir.ResilientFunctions.Domain.Exceptions;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using Shouldly;
using TUnit.Core;
using static UnitTests.DataBridge.CoreRepositoryPersistenceTests;

namespace UnitTests.DataBridge;

public sealed class WorkflowPersistenceTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static readonly ComplimentaryState State = new(() => null, TimeSpan.FromSeconds(10).Ticks);
    private static StoredId Id(StoredType type, string instance = "instance") => new(type, StoredInstance.Create(instance));
    private static StoredMessage Message(string value) => new(Bytes(value), Bytes("type"), "key");
    private static string PostgresConnection(Fixture f) => new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES"))
        { Database = new NpgsqlConnectionStringBuilder(f.Db.Database.GetConnectionString()).Database, SearchPath = "cleipnir,public" }.ConnectionString;
    internal static async Task<IFunctionStore> Store(Fixture f)
    {
        IFunctionStore store;
        if (f.Database.Provider == PersistenceProvider.Sqlite)
        {
            using var scope = f.ScopeFactory.CreateScope();
            store = new SqliteFunctionStore(scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>());
        }
        else
        {
            store = new PostgreSqlFunctionStore(PostgresConnection(f), "flows");
        }
        await store.Initialize();
        return store;
    }

    [Test]
    public Task Creation_Progress_Completion_Failure_And_Epochs_Survive_Reopen() => Both(async f =>
    {
        var store = await Store(f);
        var type = await store.TypeStore.InsertOrGetStoredType(new("creation"));
        var id = Id(type);
        var parent = Id(type, "parent");
        var effect = StoredEffect.CreateCompleted(EffectId.CreateWithRootContext("progress"), Bytes("saved"));
        (await store.CreateFunction(id, new("instance"), Bytes("param"), 10, null, 1, parent, [effect], [Message("first")])).ShouldBeTrue();
        (await store.CreateFunction(id, new("duplicate"), Bytes("wrong"), 100, null, 100, null, [effect], [Message("wrong")])).ShouldBeFalse();
        store = await Store(f);
        var flow = (await store.GetFunction(id)).ShouldNotBeNull();
        flow.Parameter.ShouldBe(Bytes("param")); flow.ParentId.ShouldBe(parent); flow.HumanInstanceId.ShouldBe("instance");
        (await store.EffectsStore.GetEffectResults(id)).Single().Result.ShouldBe(Bytes("saved"));
        (await store.MessageStore.GetMessages(id, 0)).Single().MessageContent.ShouldBe(Bytes("first"));
        (await store.SetParameters(id, Bytes("updated"), null, 9)).ShouldBeFalse();
        (await store.SetParameters(id, Bytes("updated"), null, 0)).ShouldBeTrue();
        (await store.RenewLeases([new(id, 0), new(id, 1)], 20)).ShouldBe(1);
        (await store.SucceedFunction(id, Bytes("result"), 30, 0, null, null, State)).ShouldBeFalse();
        (await store.SucceedFunction(id, Bytes("result"), 30, 1, null, null, State)).ShouldBeTrue();
        store = await Store(f);
        (await store.GetSucceededFunctions(type, 29)).ShouldBeEmpty();
        (await store.GetSucceededFunctions(type, 30)).ShouldBe([id.Instance]);
        (await store.GetFunction(id))!.Result.ShouldBe(Bytes("result"));
        var failed = Id(type, "failed");
        await store.CreateFunction(failed, new("failed"), null, 0, null, 0, null);
        var exception = new StoredException("failure", "stack", "type");
        (await store.FailFunction(failed, exception, 50, 0, null, null, State)).ShouldBeTrue();
        (await (await Store(f)).GetFunction(failed))!.Exception.ShouldBe(exception);
        (await store.GetInstances(type, Status.Failed)).ShouldBe([failed.Instance]);
        (await store.GetFunctionsStatus([id, failed, Id(type, "missing")])).Count.ShouldBe(2);
    });

    [Test]
    public Task Suspend_Interrupt_Postpone_And_Recovery_Prevent_Lost_Wakeups() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("wake")); var id = Id(type);
        await store.CreateFunction(id, new("instance"), null, 1, null, 0, null);
        (await store.SuspendFunction(id, 2, 0, null, null, State)).ShouldBeTrue();
        (await store.GetExpiredFunctions(long.MaxValue)).ShouldBeEmpty();
        await store.MessageStore.AppendMessages([new StoredIdAndMessage(id, Message("wake"))]);
        store = await Store(f);
        (await store.Interrupted(id)).ShouldBe(true);
        (await store.GetFunction(id))!.Status.ShouldBe(Status.Postponed);
        (await store.SuspendFunction(id, 3, 0, null, null, State)).ShouldBeFalse();
        (await store.PostponeFunction(id, 100, 3, false, 0, null, null, State)).ShouldBeFalse();
        (await store.GetExpiredFunctions(0)).Single().FlowId.ShouldBe(id);
        var restarted = (await store.RestartExecution(id, 0, 50)).ShouldNotBeNull();
        restarted.StoredFlow.Epoch.ShouldBe(1); restarted.StoredFlow.Interrupted.ShouldBeFalse(); restarted.Messages.Count.ShouldBe(1);
        (await store.RestartExecution(id, 0, 50)).ShouldBeNull();
        (await store.PostponeFunction(id, 100, 4, false, 1, null, null, State)).ShouldBeTrue();
        (await store.GetExpiredFunctions(99)).ShouldBeEmpty();
        (await store.GetExpiredFunctions(100)).Single().Epoch.ShouldBe(1);
        (await store.Interrupt(id, onlyIfExecuting: true)).ShouldBeFalse();
        (await store.Interrupt(id, onlyIfExecuting: false)).ShouldBeTrue();
        (await store.Interrupted(Id(type, "missing"))).ShouldBeNull();
    });

    [Test]
    public Task Messages_Effects_Timeouts_Correlations_And_Utilities_Are_Durable() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("auxiliary")); var id = Id(type);
        await store.CreateFunction(id, new("instance"), null, 0, null, 0, null);
        (await store.MessageStore.AppendMessage(id, Message("one")))!.Status.ShouldBe(Status.Executing);
        await store.MessageStore.AppendMessages([new StoredIdAndMessageWithPosition(id, Message("two"), 1)], false);
        (await store.MessageStore.ReplaceMessage(id, 1, Message("replaced"))).ShouldBeTrue();
        (await store.MessageStore.ReplaceMessage(id, 5, Message("absent"))).ShouldBeFalse();
        (await store.MessageStore.GetMaxPositions([id, Id(type,"missing")]))[id].ShouldBe(1);
        var effect = StoredEffect.CreateStarted(EffectId.CreateWithRootContext("dot.\\effect"));
        await store.EffectsStore.SetEffectResults(id, [effect.ToStoredChange(id, CrudOperation.Insert)]);
        effect = StoredEffect.CreateCompleted(effect.EffectId, Bytes("complete"));
        await store.EffectsStore.SetEffectResults(id, [effect.ToStoredChange(id, CrudOperation.Update)]);
        await store.TimeoutStore.UpsertTimeout(new(id, effect.EffectId, 10), false);
        await store.TimeoutStore.UpsertTimeout(new(id, effect.EffectId, 20), false);
        (await store.TimeoutStore.GetTimeouts(10)).Single().Expiry.ShouldBe(10);
        await store.TimeoutStore.UpsertTimeout(new(id, effect.EffectId, 30), true);
        await store.CorrelationStore.SetCorrelation(id, "correlation"); await store.CorrelationStore.SetCorrelation(id, "correlation");
        var register = store.Utilities.Register;
        (await register.SetIfEmpty("group", "key", "first")).ShouldBeTrue();
        (await register.SetIfEmpty("group", "key", "wrong")).ShouldBeFalse();
        (await register.CompareAndSwap("group", "key", "second", "wrong")).ShouldBeFalse();
        (await register.CompareAndSwap("group", "key", "second", "first")).ShouldBeTrue();
        store = await Store(f);
        (await store.MessageStore.GetMessages(id, 1)).Single().MessageContent.ShouldBe(Bytes("replaced"));
        var restoredEffect = (await store.EffectsStore.GetEffectResults(id)).Single();
        restoredEffect.EffectId.ShouldBe(effect.EffectId); restoredEffect.WorkStatus.ShouldBe(WorkStatus.Completed); restoredEffect.Result.ShouldBe(Bytes("complete"));
        (await store.TimeoutStore.GetTimeouts(id)).Single().Expiry.ShouldBe(30);
        (await store.CorrelationStore.GetCorrelations("correlation")).ShouldBe([id]);
        (await store.CorrelationStore.GetCorrelations(type, "correlation")).ShouldBe([id.Instance]);
        (await store.CorrelationStore.GetCorrelations(id)).ShouldBe(["correlation"]);
        (await store.Utilities.Register.Get("group", "key")).ShouldBe("second");
        (await store.Utilities.Register.Exists("group", "key")).ShouldBeTrue();
        (await store.Utilities.Register.Delete("group", "key", "wrong")).ShouldBeFalse();
        (await store.Utilities.Register.Delete("group", "key", "second")).ShouldBeTrue();
        await store.EffectsStore.DeleteEffectResults(id, [effect.StoredEffectId]);
        await store.TimeoutStore.RemoveTimeout(id, effect.EffectId);
        await store.CorrelationStore.RemoveCorrelation(id, "correlation");
        await store.MessageStore.Truncate(id);
        (await store.EffectsStore.GetEffectResults(id)).ShouldBeEmpty();
        (await store.TimeoutStore.GetTimeouts(id)).ShouldBeEmpty();
        (await store.CorrelationStore.GetCorrelations(id)).ShouldBeEmpty();
        (await store.MessageStore.GetMessages(id,0)).ShouldBeEmpty();
    });

    [Test]
    public Task Concurrent_Writers_Allocate_Ordered_Messages_And_One_Restart() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("race")); var id = Id(type);
        var created = await Task.WhenAll(Enumerable.Range(0,6).Select(_ => Task.Run(() => store.CreateFunction(id, new("instance"), null, 0, null, 0, null))));
        created.Count(x => x).ShouldBe(1);
        await Task.WhenAll(Enumerable.Range(0,12).Select(n => Task.Run(() => store.MessageStore.AppendMessage(id, Message(n.ToString())))));
        (await store.MessageStore.GetMessages(id,0)).Count.ShouldBe(12);
        (await store.MessageStore.GetMaxPositions([id]))[id].ShouldBe(11);
        var restarts = await Task.WhenAll(Enumerable.Range(0,6).Select(_ => Task.Run(() => store.RestartExecution(id,0,100))));
        restarts.Count(x => x is not null).ShouldBe(1);
        var types = await Task.WhenAll(Enumerable.Range(0,6).Select(_ => Task.Run(() => store.TypeStore.InsertOrGetStoredType(new("same")))));
        types.Distinct().Count().ShouldBe(1);
    });

    [Test]
    public Task Semaphore_Queue_And_Bulk_Scheduling_Survive_Restart() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("semaphore"));
        var a = Id(type,"a"); var b=Id(type,"b"); var c=Id(type,"c");
        await store.BulkScheduleFunctions([new(a,"a",Bytes("a")),new(b,"b",null),new(a,"wrong",null)],c);
        (await store.GetExpiredFunctions(0)).Count.ShouldBe(2);
        (await store.SemaphoreStore.Acquire("group","instance",a,1)).ShouldBeTrue();
        (await store.SemaphoreStore.Acquire("group","instance",a,1)).ShouldBeTrue();
        (await store.SemaphoreStore.Acquire("group","instance",b,1)).ShouldBeFalse();
        store = await Store(f);
        (await store.SemaphoreStore.GetQueued("group","instance",10)).ShouldBe([a,b]);
        (await store.SemaphoreStore.Release("group","instance",a,1)).ShouldBe([b]);
        (await store.SemaphoreStore.Acquire("group","instance",b,1)).ShouldBeTrue();
        (await store.GetFunction(a))!.Parameter.ShouldBe(Bytes("a"));
    });

    [Test]
    public Task Cancellation_Through_Control_Panel_Deletes_Durable_State() => Both(async f =>
    {
        var store = await Store(f);
        using var registry = new FunctionsRegistry(store, new Settings(enableWatchdogs: false, serializer: new NodaTimeFlowSerializer()));
        var registration = registry.RegisterFunc("cancel", (string value) => Task.FromResult(value.ToUpperInvariant()));
        (await registration.Invoke("instance", "value")).ShouldBe("VALUE");
        var id = registration.MapToStoredId("instance");
        await store.MessageStore.AppendMessage(id, Message("cleanup"));
        await store.EffectsStore.SetEffectResult(id, StoredEffect.CreateStarted(EffectId.CreateWithRootContext("effect")));
        await store.TimeoutStore.UpsertTimeout(new(id, EffectId.CreateWithRootContext("timeout"),10),false);
        await store.CorrelationStore.SetCorrelation(id,"correlation");
        var panel = (await registration.ControlPanel("instance")).ShouldNotBeNull();
        await panel.Delete();
        store = await Store(f);
        (await store.GetFunction(id)).ShouldBeNull();
        (await store.GetInstances(id.Type)).ShouldBeEmpty();
        (await store.MessageStore.GetMessages(id,0)).ShouldBeEmpty();
        (await store.EffectsStore.GetEffectResults(id)).ShouldBeEmpty();
        (await store.TimeoutStore.GetTimeouts(id)).ShouldBeEmpty();
        (await store.CorrelationStore.GetCorrelations(id)).ShouldBeEmpty();
    });

    [Test]
    public Task Serializer_Preserves_Shared_Workflow_Message_Timestamps() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("serializer")); var id = Id(type);
        var serializer = new NodaTimeFlowSerializer();
        var message = new DownloadRequested { JobId=Guid.NewGuid(), CorrelationId=Guid.NewGuid(), SourceUrl="https://test", StorageKey="default", RequestedBy="alice", OccurredAt=Instant.FromUnixTimeTicks(-12345670), MessageId=Guid.NewGuid(), OperationKey="test" };
        var serialized = serializer.SerializeMessage(message, typeof(DownloadRequested));
        await store.MessageStore.AppendMessage(id, new(serialized.Content, serialized.Type));
        var stored = (await (await Store(f)).MessageStore.GetMessages(id,0)).Single();
        var restored = (DownloadRequested)serializer.DeserializeMessage(stored.MessageContent, stored.MessageType);
        restored.OccurredAt.ShouldBe(message.OccurredAt); restored.JobId.ShouldBe(message.JobId);
    });

    [Test]
    public Task Repeated_Schema_Initialization_And_Migration_Preserve_Workflows() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("migration")); var id = Id(type);
        await store.CreateFunction(id,new("instance"),Bytes("param"),0,0,0,null);
        await store.Migrator.MigrateToLatestSchema(); await store.Initialize();
        store = await Store(f);
        (await store.GetFunction(id))!.Parameter.ShouldBe(Bytes("param"));
        (await store.TypeStore.InsertOrGetStoredType(new("migration"))).ShouldBe(type);
        (await store.TypeStore.GetAllFlowTypes())[new("migration")].ShouldBe(type);
    });
    private static Settings RecoverySettings(ConcurrentQueue<Exception> errors, bool watchdogs = true) => new(
        unhandledExceptionHandler: errors.Enqueue, enableWatchdogs: watchdogs,
        leaseLength: TimeSpan.FromMilliseconds(500), watchdogCheckFrequency: TimeSpan.FromMilliseconds(30),
        delayStartup: TimeSpan.Zero, serializer: new NodaTimeFlowSerializer());

    // Run only in a subprocess started by the crash test; ordinary suite execution is a no-op.
    [Test]
    public async Task CrashProcessProbe()
    {
        var connection = Environment.GetEnvironmentVariable("FROSTSTREAM_CRASH_STORE");
        if (connection is null) return;
        var marker = Environment.GetEnvironmentVariable("FROSTSTREAM_CRASH_MARKER")!;
        IFunctionStore store = Environment.GetEnvironmentVariable("FROSTSTREAM_CRASH_PROVIDER") == "Postgres"
            ? new PostgreSqlFunctionStore(connection, "flows")
            : new SqliteFunctionStore(new SqliteConnectionFactory(new PersistenceOptions(PersistenceProvider.Sqlite, connection, 2)));
        await store.Initialize();
        var errors = new ConcurrentQueue<Exception>();
        using var registry = new FunctionsRegistry(store, RecoverySettings(errors, false));
        var registration = registry.RegisterFunc<string,string>("crash-import", async (value, workflow) =>
        {
            await workflow.Effect.Capture("persisted-progress", () => File.AppendAllText(marker + ".effect", "once"));
            File.WriteAllText(marker, "ready");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return value;
        });
        await registration.Invoke("import-attempt", "payload");
    }

    [Test]
    public Task Killed_Process_Is_Recovered_By_Watchdog_Without_Repeating_Completed_Effect() => Both(async f =>
    {
        var store = await Store(f);
        var marker = Path.Combine(Path.GetTempPath(), "froststream-crash-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(WorkflowPersistenceTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter"); start.ArgumentList.Add("/*/*/WorkflowPersistenceTests/CrashProcessProbe");
        start.Environment["FROSTSTREAM_CRASH_PROVIDER"] = f.Database.Provider.ToString();
        start.Environment["FROSTSTREAM_CRASH_MARKER"] = marker;
        start.Environment["FROSTSTREAM_CRASH_STORE"] = f.Database.Provider == PersistenceProvider.Postgres
            ? PostgresConnection(f)
            : new SqliteConnectionStringBuilder(f.Db.Database.GetConnectionString()).DataSource;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(marker))
            {
                if (process.HasExited) throw new InvalidOperationException("Crash probe exited: " + await stdout + await stderr);
                if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Crash probe did not persist progress.");
                await Task.Delay(30);
            }
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
            store = await Store(f);
            var errors = new ConcurrentQueue<Exception>();
            using var registry = new FunctionsRegistry(store, RecoverySettings(errors));
            var registration = registry.RegisterFunc<string,string>("crash-import", async (value, workflow) =>
            {
                await workflow.Effect.Capture("persisted-progress", () => File.AppendAllText(marker + ".effect", "repeated"));
                return value.ToUpperInvariant();
            });
            var id = registration.MapToStoredId("import-attempt");
            timeout.Restart();
            while ((await store.GetFunction(id))?.Status != Status.Succeeded)
            {
                errors.ShouldBeEmpty();
                if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Watchdog did not recover the killed process.");
                await Task.Delay(30);
            }
            (await registration.Invoke("import-attempt", "ignored")).ShouldBe("PAYLOAD");
            File.ReadAllText(marker + ".effect").ShouldBe("once");
            (await store.GetFunction(id))!.Epoch.ShouldBe(1);
            errors.ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            File.Delete(marker); File.Delete(marker + ".effect");
        }
    });

    [Test]
    public Task Create_And_Message_Batch_Failures_Roll_Back_All_Writes() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("rollback")); var id = Id(type);
        var effect = StoredEffect.CreateCompleted(EffectId.CreateWithRootContext("duplicate"));
        (await store.CreateFunction(id, new("instance"), null, 0, null, 0, null, [effect,effect], [Message("must rollback")])).ShouldBeFalse();
        (await store.GetFunction(id)).ShouldBeNull();
        (await store.EffectsStore.GetEffectResults(id)).ShouldBeEmpty();
        (await store.MessageStore.GetMessages(id,0)).ShouldBeEmpty();
        await store.CreateFunction(id,new("instance"),null,0,null,0,null);
        await store.SuspendFunction(id,0,0,null,null,State);
        await Should.ThrowAsync<System.Data.Common.DbException>(() => store.MessageStore.AppendMessages([
            new StoredIdAndMessageWithPosition(id,Message("first"),0), new StoredIdAndMessageWithPosition(id,Message("conflict"),0)],true));
        (await store.MessageStore.GetMessages(id,0)).ShouldBeEmpty();
        (await store.GetFunction(id))!.Status.ShouldBe(Status.Suspended);
        (await store.Interrupted(id)).ShouldBe(false);
    });

    [Test]
    public Task Cancelled_Suspended_Workflow_Stays_Failed_After_Restart() => Both(async f =>
    {
        var store = await Store(f); StoredId id;
        var errors = new ConcurrentQueue<Exception>();
        using (var registry = new FunctionsRegistry(store, RecoverySettings(errors,false)))
        {
            var registration = registry.RegisterFunc<string,string>("cancel-suspended", async (value, workflow) =>
                await workflow.Messages.FirstOfType<string>(maxWait: TimeSpan.Zero));
            await Should.ThrowAsync<InvocationSuspendedException>(() => registration.Invoke("instance","param"));
            id = registration.MapToStoredId("instance");
            var panel = (await registration.ControlPanel("instance")).ShouldNotBeNull();
            panel.Status.ShouldBe(Status.Suspended);
            await panel.Fail(new OperationCanceledException("cancelled by user"));
        }
        store = await Store(f);
        (await store.GetFunction(id))!.Status.ShouldBe(Status.Failed);
        (await store.GetFunction(id))!.Exception!.ExceptionType.ShouldContain("OperationCanceledException");
        (await store.GetExpiredFunctions(long.MaxValue)).ShouldBeEmpty();
        using (var registry = new FunctionsRegistry(store, RecoverySettings(errors)))
        {
            var invoked = false;
            var registration = registry.RegisterFunc<string,string>("cancel-suspended", value => { invoked = true; return Task.FromResult(value); });
            var panel = (await registration.ControlPanel("instance")).ShouldNotBeNull();
            await panel.Refresh(); panel.Status.ShouldBe(Status.Failed); invoked.ShouldBeFalse();
            await panel.Delete();
        }
        (await (await Store(f)).GetFunction(id)).ShouldBeNull(); errors.ShouldBeEmpty();
    });

    [Test]
    public Task Retention_Finds_Only_Terminal_Orphans_And_Selected_Import_Attempts() => Both(async f =>
    {
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("retention"));
        var group = Guid.NewGuid(); var session = Guid.NewGuid(); var item = Guid.NewGuid();
        f.Db.DownloadGroups.Add(new DownloadGroupEntity { GroupId=group, CorrelationId=Guid.NewGuid(), SourceUrl="https://test", StorageKey="default", RequestedBy="alice" });
        f.Db.ImportSessions.Add(new ImportSessionEntity { SessionId=session, CorrelationId=Guid.NewGuid(), SourceRoot="incoming", SourceKind=ImportSessionSourceKind.WorkerIncoming, StorageKey="default" });
        f.Db.ImportSessionItems.Add(new() { ItemId=item, SessionId=session, RelativePath="file.mp4", FileName="file.mp4" });
        await f.Db.SaveChangesAsync();
        var groupInstance = group.ToString("N").ToUpperInvariant();
        var itemInstance = item.ToString("N").ToUpperInvariant() + "/attempt-12";
        var orphanRun = Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N");
        var orphanGroup = Guid.NewGuid().ToString("N");
        var orphanImport = Guid.NewGuid().ToString("N") + "/attempt-99";
        var live = Guid.NewGuid().ToString("N");
        foreach (var human in new[] { groupInstance,itemInstance,orphanRun,orphanGroup,orphanImport,live,"invalid",item.ToString("N")+"/attempt-bad" })
        {
            var id = Id(type,human);
            await store.CreateFunction(id,new(human),null,0,null,0,null);
            if (human != live) await store.SucceedFunction(id,null,0,0,null,null,State);
        }
        var query = new WorkflowRetentionQueries(f.Database);
        var orphans = new[] { orphanRun,orphanGroup,orphanImport }.Order(StringComparer.Ordinal).ToArray();
        (await query.FindOrphansAsync(100,default)).ShouldBe(orphans);
        (await query.FindOrphansAsync(1,default)).ShouldBe(orphans.Take(1));
        (await query.FindImportInstancesAsync([item,item,Guid.NewGuid()],default)).ShouldBe([itemInstance]);
        (await query.FindImportInstancesAsync([],default)).ShouldBeEmpty();
        await Should.ThrowAsync<OperationCanceledException>(() => query.FindOrphansAsync(100,new CancellationToken(true)));
    });

    [Test]
    public async Task Sqlite_Empty_Workflow_Version_Upgrades_And_Future_Versions_Are_Rejected()
    {
        var directory = Path.Combine(Path.GetTempPath(),"workflow-schema-"+Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite,Path.Combine(directory,"db"),1));
        try
        {
            using (var connection = factory.OpenConnection())
            using (var command = connection.CreateCommand())
            { command.CommandText="CREATE TABLE cleipnir_schema(id INTEGER PRIMARY KEY CHECK(id=1),version INTEGER NOT NULL); INSERT INTO cleipnir_schema VALUES(1,0);"; command.ExecuteNonQuery(); }
            var store = new SqliteFunctionStore(factory); await store.MessageStore.Initialize();
            var type = await store.TypeStore.InsertOrGetStoredType(new("schema")); var id = Id(type);
            await store.CreateFunction(id,new("instance"),Bytes("preserve"),0,null,0,null);
            using (var connection = factory.OpenConnection())
            using (var command = connection.CreateCommand())
            { command.CommandText="UPDATE cleipnir_schema SET version=2"; command.ExecuteNonQuery(); }
            await Should.ThrowAsync<InvalidOperationException>(() => new SqliteFunctionStore(factory).Initialize());
            (await store.GetFunction(id))!.Parameter.ShouldBe(Bytes("preserve"));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Test]
    public async Task Sqlite_Held_Writer_Times_Out_Without_Partial_Workflow_State()
    {
        await using var f = await Fixture.Create(false);
        using var scope = f.ScopeFactory.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
        var store = await Store(f); var type = await store.TypeStore.InsertOrGetStoredType(new("busy")); var id=Id(type);
        using var connection = factory.OpenConnection(); using var transaction=connection.BeginTransaction(deferred:false);
        var timer=Stopwatch.StartNew();
        var error = await Should.ThrowAsync<SqliteException>(() => Task.Run(() => store.CreateFunction(id,new("instance"),null,0,null,0,null)));
        error.SqliteErrorCode.ShouldBe(5); timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        transaction.Rollback(); (await store.GetFunction(id)).ShouldBeNull();
        (await store.CreateFunction(id,new("instance"),null,0,null,0,null)).ShouldBeTrue();
    }

}
