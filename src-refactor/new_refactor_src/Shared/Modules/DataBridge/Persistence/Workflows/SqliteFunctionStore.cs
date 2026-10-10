using System.Text.Json;
using Cleipnir.ResilientFunctions.CoreRuntime.Invocation;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Messaging;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using static DataBridge.Persistence.Workflows.SqliteWorkflowDatabase;

namespace DataBridge.Persistence.Workflows;

/// <summary>Durable implementation of the pinned ResilientFunctions 4.2.5 contract.</summary>
public sealed class SqliteFunctionStore : IFunctionStore, IMigrator
{
    private readonly SqliteWorkflowDatabase db;
    private readonly SqliteWorkflowStores stores;
    public ITypeStore TypeStore => stores;
    public IMessageStore MessageStore => stores;
    public IEffectsStore EffectsStore => stores;
    public ITimeoutStore TimeoutStore => stores;
    public ICorrelationStore CorrelationStore => stores;
    public ISemaphoreStore SemaphoreStore => stores;
    public Cleipnir.ResilientFunctions.CoreRuntime.Invocation.Utilities Utilities { get; }
    public IMigrator Migrator => this;

    public SqliteFunctionStore(SqliteConnectionFactory connections)
    {
        db = new(connections);
        stores = new(db, Initialize);
        Utilities = new(stores);
    }

    public Task Initialize() => MigrateToLatestSchema();
    public Task MigrateToLatestSchema() => db.Write(s =>
    {
        s.Execute("CREATE TABLE IF NOT EXISTS cleipnir_schema (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL);");
        var version = Convert.ToInt32(s.Scalar("SELECT version FROM cleipnir_schema WHERE id=1") ?? 0);
        if (version is < 0 or > 1) throw new InvalidOperationException($"Unsupported SQLite workflow schema version {version}.");
        if (version == 1) return true;
        s.Execute("""
            CREATE TABLE cleipnir_flows (
                type INTEGER NOT NULL, instance TEXT NOT NULL, epoch INTEGER NOT NULL DEFAULT 0,
                expires INTEGER NOT NULL, interrupted INTEGER NOT NULL DEFAULT 0,
                param_json BLOB, status INTEGER NOT NULL, result_json BLOB, exception_json TEXT,
                timestamp INTEGER NOT NULL, human_instance_id TEXT NOT NULL, parent TEXT,
                PRIMARY KEY(type, instance));
            CREATE INDEX cleipnir_flows_expires ON cleipnir_flows(status, expires, type, instance, epoch);
            CREATE INDEX cleipnir_flows_terminal ON cleipnir_flows(status, human_instance_id);
            CREATE TABLE cleipnir_types (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
            CREATE TABLE cleipnir_effects (
                type INTEGER NOT NULL, instance TEXT NOT NULL, id_hash TEXT NOT NULL,
                effect_id TEXT NOT NULL, status INTEGER NOT NULL, result BLOB, exception TEXT,
                PRIMARY KEY(type, instance, id_hash));
            CREATE TABLE cleipnir_messages (
                type INTEGER NOT NULL, instance TEXT NOT NULL, position INTEGER NOT NULL,
                message_json BLOB NOT NULL, message_type BLOB NOT NULL, idempotency_key TEXT,
                PRIMARY KEY(type, instance, position));
            CREATE TABLE cleipnir_timeouts (
                type INTEGER NOT NULL, instance TEXT NOT NULL, timeout_id TEXT NOT NULL, expiry INTEGER NOT NULL,
                PRIMARY KEY(type, instance, timeout_id));
            CREATE INDEX cleipnir_timeouts_expiry ON cleipnir_timeouts(expiry);
            CREATE TABLE cleipnir_correlations (
                type INTEGER NOT NULL, instance TEXT NOT NULL, correlation TEXT NOT NULL,
                PRIMARY KEY(type, instance, correlation));
            CREATE INDEX cleipnir_correlations_value ON cleipnir_correlations(correlation, type);
            CREATE TABLE cleipnir_semaphores (
                groupname TEXT NOT NULL, instance TEXT NOT NULL, position INTEGER NOT NULL, owner TEXT NOT NULL,
                PRIMARY KEY(groupname, instance, position), UNIQUE(groupname, instance, owner));
            CREATE TABLE cleipnir_register (
                registertype INTEGER NOT NULL, groupname TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
                PRIMARY KEY(registertype, groupname, name));
            INSERT INTO cleipnir_schema VALUES(1,1) ON CONFLICT(id) DO UPDATE SET version=excluded.version;
            """);
        return true;
    });

    private static int Create(Session s, StoredId id, string human, byte[]? param, long lease, long? postpone, long timestamp, StoredId? parent)
        => s.Execute("""
            INSERT INTO cleipnir_flows(type,instance,status,param_json,expires,timestamp,human_instance_id,parent)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8) ON CONFLICT(type,instance) DO NOTHING
            """, id.Type.Value, id.Instance.Value, (int)(postpone is null ? Status.Executing : Status.Postponed), param, postpone ?? lease, timestamp, human, parent?.Serialize());

    public async Task<bool> CreateFunction(StoredId storedId, FlowInstance humanInstanceId, byte[]? param, long leaseExpiration,
        long? postponeUntil, long timestamp, StoredId? parent, IReadOnlyList<StoredEffect>? effects = null, IReadOnlyList<StoredMessage>? messages = null)
    {
        try
        {
            return await db.Write(s =>
            {
                if (Create(s, storedId, humanInstanceId.Value, param, leaseExpiration, postponeUntil, timestamp, parent) == 0) return false;
                foreach (var effect in effects ?? []) SqliteWorkflowStores.InsertEffect(s, storedId, effect);
                foreach (var message in messages ?? []) SqliteWorkflowStores.Append(s, storedId, message);
                return true;
            });
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode is 1555 or 2067)
        {
            // The transaction has already rolled back, including initial effects/messages.
            return false;
        }
    }

    public Task BulkScheduleFunctions(IEnumerable<IdWithParam> functionsWithParam, StoredId? parent)
    {
        // Evaluate caller-provided sequences before taking SQLite writer ownership.
        var functions = functionsWithParam.ToArray();
        return db.Write(s =>
        {
            foreach (var f in functions) Create(s, f.StoredId, f.HumanInstanceId, f.Param, 0, 0, 0, parent);
            return true;
        });
    }

    public Task<StoredFlowWithEffectsAndMessages?> RestartExecution(StoredId storedId, int expectedEpoch, long leaseExpiration)
        => db.Write<StoredFlowWithEffectsAndMessages?>(s =>
        {
            if (s.Execute("UPDATE cleipnir_flows SET epoch=epoch+1,status=$1,expires=$2,interrupted=0 WHERE type=$3 AND instance=$4 AND epoch=$5",
                    (int)Status.Executing, leaseExpiration, storedId.Type.Value, storedId.Instance.Value, expectedEpoch) == 0) return null;
            return new(ReadFlow(s, storedId)!, SqliteWorkflowStores.ReadEffects(s, storedId), SqliteWorkflowStores.ReadMessages(s, storedId, 0));
        });

    public Task<int> RenewLeases(IReadOnlyList<LeaseUpdate> leaseUpdates, long leaseExpiration) => db.Write(s =>
        leaseUpdates.Sum(u => s.Execute("UPDATE cleipnir_flows SET expires=$1 WHERE type=$2 AND instance=$3 AND epoch=$4", leaseExpiration, u.StoredId.Type.Value, u.StoredId.Instance.Value, u.ExpectedEpoch)));
    public Task<IReadOnlyList<IdAndEpoch>> GetExpiredFunctions(long expiresBefore) => db.Read<IReadOnlyList<IdAndEpoch>>(s =>
        s.Query("SELECT type,instance,epoch FROM cleipnir_flows WHERE expires <= $1 AND status IN ($2,$3)", r => new IdAndEpoch(Id(r), r.GetInt32(2)), expiresBefore, (int)Status.Executing, (int)Status.Postponed));
    public Task<IReadOnlyList<StoredInstance>> GetSucceededFunctions(StoredType storedType, long completedBefore) => db.Read<IReadOnlyList<StoredInstance>>(s =>
        s.Query("SELECT instance FROM cleipnir_flows WHERE type=$1 AND status=$2 AND timestamp <= $3", r => new StoredInstance(r.GetGuid(0)), storedType.Value, (int)Status.Succeeded, completedBefore));

    public Task<bool> SetParameters(StoredId storedId, byte[]? param, byte[]? result, int expectedEpoch) => db.Write(s =>
        s.Execute("UPDATE cleipnir_flows SET param_json=$1,result_json=$2,epoch=epoch+1 WHERE type=$3 AND instance=$4 AND epoch=$5", param, result, storedId.Type.Value, storedId.Instance.Value, expectedEpoch) == 1);
    public Task<bool> SetFunctionState(StoredId storedId, Status status, byte[]? param, byte[]? result, StoredException? storedException, long expires, int expectedEpoch) => db.Write(s =>
        s.Execute("UPDATE cleipnir_flows SET status=$1,param_json=$2,result_json=$3,exception_json=$4,expires=$5,epoch=epoch+1 WHERE type=$6 AND instance=$7 AND epoch=$8",
            (int)status, param, result, Json(storedException), expires, storedId.Type.Value, storedId.Instance.Value, expectedEpoch) == 1);

    // Like the pinned PostgreSQL store, transitions preserve epoch; administrative changes and restart advance it.
    private Task<bool> Transition(StoredId id, Status status, byte[]? result, StoredException? exception, long? expires, long timestamp, int epoch,
        bool requireUninterrupted, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages)
        => db.Write(s =>
        {
            if (s.Execute("""
                UPDATE cleipnir_flows SET status=$1,result_json=CASE WHEN $1=$2 THEN $3 ELSE result_json END,
                    exception_json=CASE WHEN $1=$4 THEN $5 ELSE exception_json END,expires=COALESCE($6,expires),timestamp=$7
                WHERE type=$8 AND instance=$9 AND epoch=$10 AND ($11=0 OR interrupted=0)
                """, (int)status, (int)Status.Succeeded, result, (int)Status.Failed, Json(exception), expires, timestamp, id.Type.Value, id.Instance.Value, epoch, requireUninterrupted) == 0) return false;
            Persist(s, id, effects, messages);
            return true;
        });
    public Task<bool> SucceedFunction(StoredId storedId, byte[]? result, long timestamp, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState)
        => Transition(storedId, Status.Succeeded, result, null, null, timestamp, expectedEpoch, false, effects, messages);
    public Task<bool> FailFunction(StoredId storedId, StoredException storedException, long timestamp, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState)
        => Transition(storedId, Status.Failed, null, storedException, null, timestamp, expectedEpoch, false, effects, messages);
    public Task<bool> PostponeFunction(StoredId storedId, long postponeUntil, long timestamp, bool ignoreInterrupted, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState)
        => Transition(storedId, Status.Postponed, null, null, postponeUntil, timestamp, expectedEpoch, !ignoreInterrupted, effects, messages);
    public Task<bool> SuspendFunction(StoredId storedId, long timestamp, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState)
        => Transition(storedId, Status.Suspended, null, null, null, timestamp, expectedEpoch, true, effects, messages);

    private static void Persist(Session s, StoredId id, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages)
    {
        foreach (var effect in effects ?? []) SqliteWorkflowStores.UpsertEffect(s, id, effect);
        foreach (var message in messages ?? []) SqliteWorkflowStores.Append(s, id, message);
    }
    internal static bool Interrupt(Session s, StoredId id, bool onlyIfExecuting = false) => s.Execute("""
        UPDATE cleipnir_flows SET interrupted=1,
            expires=CASE WHEN status IN ($1,$2) THEN 0 ELSE expires END,
            status=CASE WHEN status=$1 THEN $2 ELSE status END
        WHERE type=$3 AND instance=$4 AND ($5=0 OR status=$6)
        """, (int)Status.Suspended, (int)Status.Postponed, id.Type.Value, id.Instance.Value, onlyIfExecuting, (int)Status.Executing) == 1;
    public Task<bool> Interrupt(StoredId storedId, bool onlyIfExecuting) => db.Write(s => Interrupt(s, storedId, onlyIfExecuting));
    public Task Interrupt(IReadOnlyList<StoredId> storedIds) => db.Write(s => { foreach (var id in storedIds) Interrupt(s, id); return true; });
    public Task<bool?> Interrupted(StoredId storedId) => db.Read<bool?>(s => s.Scalar("SELECT interrupted FROM cleipnir_flows WHERE type=$1 AND instance=$2", storedId.Type.Value, storedId.Instance.Value) is { } value ? Convert.ToBoolean(value) : null);
    public Task<StatusAndEpoch?> GetFunctionStatus(StoredId storedId) => db.Read(s => ReadStatus(s, storedId));
    internal static StatusAndEpoch? ReadStatus(Session s, StoredId id) => s.Query("SELECT status,epoch FROM cleipnir_flows WHERE type=$1 AND instance=$2", r => new StatusAndEpoch((Status)r.GetInt32(0), r.GetInt32(1)), id.Type.Value, id.Instance.Value).FirstOrDefault();
    public Task<IReadOnlyList<StatusAndEpochWithId>> GetFunctionsStatus(IEnumerable<StoredId> storedIds) => db.Read<IReadOnlyList<StatusAndEpochWithId>>(s =>
        storedIds.Distinct().SelectMany(id => s.Query("SELECT status,epoch,expires FROM cleipnir_flows WHERE type=$1 AND instance=$2", r => new StatusAndEpochWithId(id, (Status)r.GetInt32(0), r.GetInt32(1), r.GetInt64(2)), id.Type.Value, id.Instance.Value)).ToList());
    public Task<StoredFlow?> GetFunction(StoredId storedId) => db.Read(s => ReadFlow(s, storedId));
    private static StoredFlow? ReadFlow(Session s, StoredId id) => s.Query("""
        SELECT human_instance_id,param_json,status,result_json,exception_json,epoch,expires,timestamp,interrupted,parent
        FROM cleipnir_flows WHERE type=$1 AND instance=$2
        """, r => new StoredFlow(id, r.GetString(0), Bytes(r,1), (Status)r.GetInt32(2), Bytes(r,3), Exception(r,4), r.GetInt32(5), r.GetInt64(6), r.GetInt64(7), r.GetBoolean(8), r.IsDBNull(9) ? null : StoredId.Deserialize(r.GetString(9))), id.Type.Value, id.Instance.Value).FirstOrDefault();
    public Task<IReadOnlyList<StoredInstance>> GetInstances(StoredType storedType, Status status) => db.Read<IReadOnlyList<StoredInstance>>(s =>
        s.Query("SELECT instance FROM cleipnir_flows WHERE type=$1 AND status=$2", r => new StoredInstance(r.GetGuid(0)), storedType.Value, (int)status));
    public Task<IReadOnlyList<StoredInstance>> GetInstances(StoredType storedType) => db.Read<IReadOnlyList<StoredInstance>>(s =>
        s.Query("SELECT instance FROM cleipnir_flows WHERE type=$1", r => new StoredInstance(r.GetGuid(0)), storedType.Value));
    public Task<bool> DeleteFunction(StoredId storedId) => db.Write(s =>
    {
        foreach (var table in new[] { "effects", "messages", "timeouts", "correlations" })
            s.Execute($"DELETE FROM cleipnir_{table} WHERE type=$1 AND instance=$2", storedId.Type.Value, storedId.Instance.Value);
        s.Execute("DELETE FROM cleipnir_semaphores WHERE owner=$1", storedId.Serialize());
        return s.Execute("DELETE FROM cleipnir_flows WHERE type=$1 AND instance=$2", storedId.Type.Value, storedId.Instance.Value) == 1;
    });
    internal static StoredId Id(SqliteDataReader r) => new(new(r.GetInt32(0)), new(r.GetGuid(1)));
    internal static byte[]? Bytes(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : (byte[])r.GetValue(index);
    internal static StoredException? Exception(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : JsonSerializer.Deserialize<StoredException>(r.GetString(index));
    internal static string? Json(StoredException? exception) => exception is null ? null : JsonSerializer.Serialize(exception);
}
