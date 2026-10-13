using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Messaging;
using Cleipnir.ResilientFunctions.Storage;
using Cleipnir.ResilientFunctions.Utils;
using static DataBridge.Persistence.Workflows.SqliteWorkflowDatabase;
using static DataBridge.Persistence.Workflows.SqliteFunctionStore;

namespace DataBridge.Persistence.Workflows;

internal sealed class SqliteWorkflowStores(SqliteWorkflowDatabase db, Func<Task> initialize) : ITypeStore, IMessageStore, IEffectsStore,
    ITimeoutStore, ICorrelationStore, ISemaphoreStore, IUnderlyingRegister
{
    // The parent store creates all tables in a single versioned transaction.
    public Task Initialize() => initialize();
    public Task<StoredType> InsertOrGetStoredType(FlowType flowType) => db.Write(s =>
    {
        s.Execute("INSERT INTO cleipnir_types(name) VALUES($1) ON CONFLICT(name) DO NOTHING", flowType.Value);
        return new StoredType(Convert.ToInt32(s.Scalar("SELECT id FROM cleipnir_types WHERE name=$1", flowType.Value)));
    });
    public Task<IReadOnlyDictionary<FlowType, StoredType>> GetAllFlowTypes() => db.Read<IReadOnlyDictionary<FlowType, StoredType>>(s =>
        s.Query("SELECT name,id FROM cleipnir_types", r => (Name: new FlowType(r.GetString(0)), Id: new StoredType(r.GetInt32(1)))).ToDictionary(x => x.Name, x => x.Id));

    internal static void Append(Session s, StoredId id, StoredMessage message, int? position = null)
        => s.Execute("""
            INSERT INTO cleipnir_messages(type,instance,position,message_json,message_type,idempotency_key)
            VALUES($1,$2,COALESCE($3,(SELECT COALESCE(MAX(position),-1)+1 FROM cleipnir_messages WHERE type=$1 AND instance=$2)),$4,$5,$6)
            """, id.Type.Value, id.Instance.Value, position, message.MessageContent, message.MessageType, message.IdempotencyKey);
    internal static IReadOnlyList<StoredMessage> ReadMessages(Session s, StoredId id, int skip) => s.Query("""
        SELECT message_json,message_type,idempotency_key FROM cleipnir_messages
        WHERE type=$1 AND instance=$2 AND position >= $3 ORDER BY position
        """, r => new StoredMessage((byte[])r.GetValue(0), (byte[])r.GetValue(1), r.IsDBNull(2) ? null : r.GetString(2)), id.Type.Value, id.Instance.Value, skip);
    public Task<FunctionStatus?> AppendMessage(StoredId storedId, StoredMessage storedMessage) => db.Write<FunctionStatus?>(s =>
    {
        Append(s, storedId, storedMessage);
        var state = ReadStatus(s, storedId);
        return state is null ? null : new(state.Status, state.Epoch);
    });
    public Task AppendMessages(IReadOnlyList<StoredIdAndMessage> messages, bool interrupt = true) => db.Write(s =>
    {
        foreach (var message in messages) Append(s, message.StoredId, message.StoredMessage);
        if (interrupt) foreach (var id in messages.Select(m => m.StoredId).Distinct()) SqliteFunctionStore.Interrupt(s, id);
        return true;
    });
    public Task AppendMessages(IReadOnlyList<StoredIdAndMessageWithPosition> messages, bool interrupt) => db.Write(s =>
    {
        foreach (var message in messages) Append(s, message.StoredId, message.StoredMessage, message.Position);
        if (interrupt) foreach (var id in messages.Select(m => m.StoredId).Distinct()) SqliteFunctionStore.Interrupt(s, id);
        return true;
    });
    public Task<bool> ReplaceMessage(StoredId storedId, int position, StoredMessage storedMessage) => db.Write(s =>
        s.Execute("UPDATE cleipnir_messages SET message_json=$1,message_type=$2,idempotency_key=$3 WHERE type=$4 AND instance=$5 AND position=$6",
            storedMessage.MessageContent, storedMessage.MessageType, storedMessage.IdempotencyKey, storedId.Type.Value, storedId.Instance.Value, position) == 1);
    public Task Truncate(StoredId storedId) => Remove("messages", storedId);
    public Task<IReadOnlyList<StoredMessage>> GetMessages(StoredId storedId, int skip) => db.Read(s => ReadMessages(s, storedId, skip));
    public Task<IDictionary<StoredId, int>> GetMaxPositions(IReadOnlyList<StoredId> storedIds) => db.Read<IDictionary<StoredId,int>>(s =>
        storedIds.Distinct().ToDictionary(id => id, id => Convert.ToInt32(s.Scalar("SELECT COALESCE(MAX(position),-1) FROM cleipnir_messages WHERE type=$1 AND instance=$2", id.Type.Value, id.Instance.Value))));

    internal static void InsertEffect(Session s, StoredId id, StoredEffect effect) => s.Execute(
        "INSERT INTO cleipnir_effects(type,instance,id_hash,effect_id,status,result,exception) VALUES($1,$2,$3,$4,$5,$6,$7)",
        id.Type.Value, id.Instance.Value, effect.StoredEffectId.Value, effect.EffectId.Serialize(), (int)effect.WorkStatus, effect.Result, Json(effect.StoredException));
    internal static void UpsertEffect(Session s, StoredId id, StoredEffect effect) => s.Execute("""
        INSERT INTO cleipnir_effects(type,instance,id_hash,effect_id,status,result,exception) VALUES($1,$2,$3,$4,$5,$6,$7)
        ON CONFLICT(type,instance,id_hash) DO UPDATE SET effect_id=excluded.effect_id,status=excluded.status,result=excluded.result,exception=excluded.exception
        """, id.Type.Value, id.Instance.Value, effect.StoredEffectId.Value, effect.EffectId.Serialize(), (int)effect.WorkStatus, effect.Result, Json(effect.StoredException));
    internal static IReadOnlyList<StoredEffect> ReadEffects(Session s, StoredId id) => s.Query("SELECT effect_id,id_hash,status,result,exception FROM cleipnir_effects WHERE type=$1 AND instance=$2",
        r => new StoredEffect(EffectId.Deserialize(r.GetString(0)), new(r.GetGuid(1)), (WorkStatus)r.GetInt32(2), Bytes(r,3), Exception(r,4)), id.Type.Value, id.Instance.Value);
    public Task SetEffectResult(StoredId storedId, StoredEffect storedEffect) => db.Write(s => { UpsertEffect(s, storedId, storedEffect); return true; });
    public Task SetEffectResults(StoredId storedId, IReadOnlyList<StoredEffectChange> changes) => db.Write(s =>
    {
        foreach (var change in changes)
        {
            if (change.StoredId != storedId) throw new ArgumentException("Effect change belongs to a different workflow.", nameof(changes));
            if (change.Operation == CrudOperation.Delete) DeleteEffect(s, storedId, change.EffectId);
            else if (change.Operation == CrudOperation.Insert)
            {
                InsertEffect(s, storedId, change.StoredEffect!);
            }
            else
            {
                var effect = change.StoredEffect!;
                s.Execute("UPDATE cleipnir_effects SET status=$1,result=$2,exception=$3 WHERE type=$4 AND instance=$5 AND id_hash=$6",
                    (int)effect.WorkStatus, effect.Result, Json(effect.StoredException), storedId.Type.Value, storedId.Instance.Value, change.EffectId.Value);
            }
        }
        return true;
    });
    public Task<IReadOnlyList<StoredEffect>> GetEffectResults(StoredId storedId) => db.Read(s => ReadEffects(s, storedId));
    private static void DeleteEffect(Session s, StoredId id, StoredEffectId effect) => s.Execute("DELETE FROM cleipnir_effects WHERE type=$1 AND instance=$2 AND id_hash=$3", id.Type.Value, id.Instance.Value, effect.Value);
    public Task DeleteEffectResult(StoredId storedId, StoredEffectId effectId) => db.Write(s => { DeleteEffect(s, storedId, effectId); return true; });
    public Task DeleteEffectResults(StoredId storedId, IReadOnlyList<StoredEffectId> effectIds) => db.Write(s => { foreach (var id in effectIds) DeleteEffect(s, storedId, id); return true; });
    Task IEffectsStore.Remove(StoredId storedId) => Remove("effects", storedId);
    Task IEffectsStore.Truncate() => Truncate("effects");

    public Task UpsertTimeout(StoredTimeout storedTimeout, bool overwrite) => db.Write(s => s.Execute("""
        INSERT INTO cleipnir_timeouts(type,instance,timeout_id,expiry) VALUES($1,$2,$3,$4)
        ON CONFLICT(type,instance,timeout_id) DO UPDATE SET expiry=excluded.expiry WHERE $5
        """, storedTimeout.StoredId.Type.Value, storedTimeout.StoredId.Instance.Value, storedTimeout.TimeoutId.Serialize(), storedTimeout.Expiry, overwrite));
    public Task RemoveTimeout(StoredId storedId, EffectId timeoutId) => db.Write(s => s.Execute("DELETE FROM cleipnir_timeouts WHERE type=$1 AND instance=$2 AND timeout_id=$3", storedId.Type.Value, storedId.Instance.Value, timeoutId.Serialize()));
    Task ITimeoutStore.Remove(StoredId storedId) => Remove("timeouts", storedId);
    Task ITimeoutStore.Truncate() => Truncate("timeouts");
    public Task<IEnumerable<StoredTimeout>> GetTimeouts(long expiresBefore) => db.Read<IEnumerable<StoredTimeout>>(s => s.Query("SELECT type,instance,timeout_id,expiry FROM cleipnir_timeouts WHERE expiry <= $1", r => new StoredTimeout(Id(r), EffectId.Deserialize(r.GetString(2)), r.GetInt64(3)), expiresBefore));
    public Task<IEnumerable<StoredTimeout>> GetTimeouts(StoredId storedId) => db.Read<IEnumerable<StoredTimeout>>(s => s.Query("SELECT timeout_id,expiry FROM cleipnir_timeouts WHERE type=$1 AND instance=$2", r => new StoredTimeout(storedId, EffectId.Deserialize(r.GetString(0)), r.GetInt64(1)), storedId.Type.Value, storedId.Instance.Value));

    public Task SetCorrelation(StoredId storedId, string correlationId) => db.Write(s => s.Execute("INSERT INTO cleipnir_correlations(type,instance,correlation) VALUES($1,$2,$3) ON CONFLICT DO NOTHING", storedId.Type.Value, storedId.Instance.Value, correlationId));
    public Task<IReadOnlyList<StoredId>> GetCorrelations(string correlationId) => db.Read<IReadOnlyList<StoredId>>(s => s.Query("SELECT type,instance FROM cleipnir_correlations WHERE correlation=$1", Id, correlationId));
    public Task<IReadOnlyList<StoredInstance>> GetCorrelations(StoredType flowType, string correlationId) => db.Read<IReadOnlyList<StoredInstance>>(s => s.Query("SELECT instance FROM cleipnir_correlations WHERE type=$1 AND correlation=$2", r => new StoredInstance(r.GetGuid(0)), flowType.Value, correlationId));
    public Task<IReadOnlyList<string>> GetCorrelations(StoredId storedId) => db.Read<IReadOnlyList<string>>(s => s.Query("SELECT correlation FROM cleipnir_correlations WHERE type=$1 AND instance=$2", r => r.GetString(0), storedId.Type.Value, storedId.Instance.Value));
    public Task RemoveCorrelations(StoredId storedId) => Remove("correlations", storedId);
    public Task RemoveCorrelation(StoredId storedId, string correlationId) => db.Write(s => s.Execute("DELETE FROM cleipnir_correlations WHERE type=$1 AND instance=$2 AND correlation=$3", storedId.Type.Value, storedId.Instance.Value, correlationId));
    Task ICorrelationStore.Truncate() => Truncate("correlations");

    private static IReadOnlyList<StoredId> Queue(Session s, string group, string instance, int count) => s.Query("SELECT owner FROM cleipnir_semaphores WHERE groupname=$1 AND instance=$2 ORDER BY position LIMIT $3", r => StoredId.Deserialize(r.GetString(0)), group, instance, count);
    public Task<bool> Acquire(string group, string instance, StoredId storedId, int maximumCount) => db.Write(s =>
    {
        s.Execute("""
            INSERT INTO cleipnir_semaphores(groupname,instance,position,owner)
            VALUES($1,$2,(SELECT COALESCE(MAX(position),-1)+1 FROM cleipnir_semaphores WHERE groupname=$1 AND instance=$2),$3)
            ON CONFLICT(groupname,instance,owner) DO NOTHING
            """, group, instance, storedId.Serialize());
        return Queue(s, group, instance, maximumCount).Contains(storedId);
    });
    public Task<IReadOnlyList<StoredId>> Release(string group, string instance, StoredId storedId, int maximumCount) => db.Write(s =>
    {
        s.Execute("DELETE FROM cleipnir_semaphores WHERE groupname=$1 AND instance=$2 AND owner=$3", group, instance, storedId.Serialize());
        return Queue(s, group, instance, maximumCount);
    });
    public Task<IReadOnlyList<StoredId>> GetQueued(string group, string instance, int count) => db.Read(s => Queue(s, group, instance, count));

    public Task<bool> SetIfEmpty(RegisterType registerType, string group, string name, string value) => db.Write(s =>
        s.Execute("INSERT INTO cleipnir_register VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING", (int)registerType, group, name, value) == 1);
    public Task<bool> CompareAndSwap(RegisterType registerType, string group, string name, string newValue, string expectedValue, bool setIfEmpty = true) => db.Write(s =>
    {
        if (s.Execute("UPDATE cleipnir_register SET value=$1 WHERE registertype=$2 AND groupname=$3 AND name=$4 AND value=$5", newValue, (int)registerType, group, name, expectedValue) == 1) return true;
        return setIfEmpty && s.Execute("INSERT INTO cleipnir_register VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING", (int)registerType, group, name, newValue) == 1;
    });
    public Task<string?> Get(RegisterType registerType, string group, string name) => db.Read(s => (string?)s.Scalar("SELECT value FROM cleipnir_register WHERE registertype=$1 AND groupname=$2 AND name=$3", (int)registerType, group, name));
    public Task<bool> Delete(RegisterType registerType, string group, string name, string expectedValue) => db.Write(s => s.Execute("DELETE FROM cleipnir_register WHERE registertype=$1 AND groupname=$2 AND name=$3 AND value=$4", (int)registerType, group, name, expectedValue) == 1);
    public Task Delete(RegisterType registerType, string group, string name) => db.Write(s => s.Execute("DELETE FROM cleipnir_register WHERE registertype=$1 AND groupname=$2 AND name=$3", (int)registerType, group, name));
    public Task<bool> Exists(RegisterType registerType, string group, string name) => db.Read(s => s.Scalar("SELECT 1 FROM cleipnir_register WHERE registertype=$1 AND groupname=$2 AND name=$3", (int)registerType, group, name) is not null);
    private Task Remove(string table, StoredId id) => db.Write(s => s.Execute($"DELETE FROM cleipnir_{table} WHERE type=$1 AND instance=$2", id.Type.Value, id.Instance.Value));
    private Task Truncate(string table) => db.Write(s => s.Execute($"DELETE FROM cleipnir_{table}"));
}
