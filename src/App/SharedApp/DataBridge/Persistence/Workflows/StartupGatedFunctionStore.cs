using Cleipnir.ResilientFunctions.CoreRuntime.Invocation;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Messaging;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge.Messaging;

namespace DataBridge.Persistence.Workflows;

/// <summary>Watchdogs cannot recover old work before the blocking download startup gate has completed.</summary>
internal sealed class StartupGatedFunctionStore(IFunctionStore inner, DownloadFlowStartupState startup) : IFunctionStore
{
    public ITypeStore TypeStore => inner.TypeStore;
    public IMessageStore MessageStore => inner.MessageStore;
    public IEffectsStore EffectsStore => inner.EffectsStore;
    public ICorrelationStore CorrelationStore => inner.CorrelationStore;
    public ISemaphoreStore SemaphoreStore => inner.SemaphoreStore;
    public IMigrator Migrator => inner.Migrator;
    public Cleipnir.ResilientFunctions.CoreRuntime.Invocation.Utilities Utilities => inner.Utilities;
    public ITimeoutStore TimeoutStore { get; } = new StartupGatedTimeoutStore(inner.TimeoutStore, startup);
    public Task Initialize() => inner.Initialize();
    public Task<bool> CreateFunction(StoredId storedId, FlowInstance humanInstanceId, byte[]? param, long leaseExpiration, long? postponeUntil, long timestamp, StoredId? parent, IReadOnlyList<StoredEffect>? effects = null, IReadOnlyList<StoredMessage>? messages = null) => inner.CreateFunction(storedId, humanInstanceId, param, leaseExpiration, postponeUntil, timestamp, parent, effects, messages);
    public Task BulkScheduleFunctions(IEnumerable<IdWithParam> functionsWithParam, StoredId? parent) => inner.BulkScheduleFunctions(functionsWithParam, parent);
    public Task<StoredFlowWithEffectsAndMessages?> RestartExecution(StoredId storedId, int expectedEpoch, long leaseExpiration) => inner.RestartExecution(storedId, expectedEpoch, leaseExpiration);
    public Task<int> RenewLeases(IReadOnlyList<LeaseUpdate> leaseUpdates, long leaseExpiration) => inner.RenewLeases(leaseUpdates, leaseExpiration);
    public Task<IReadOnlyList<IdAndEpoch>> GetExpiredFunctions(long expiresBefore) => startup.IsReady ? inner.GetExpiredFunctions(expiresBefore) : Task.FromResult<IReadOnlyList<IdAndEpoch>>([]);
    public Task<IReadOnlyList<StoredInstance>> GetSucceededFunctions(StoredType storedType, long completedBefore) => inner.GetSucceededFunctions(storedType, completedBefore);
    public Task<bool> SetParameters(StoredId storedId, byte[]? param, byte[]? result, int expectedEpoch) => inner.SetParameters(storedId, param, result, expectedEpoch);
    public Task<bool> SetFunctionState(StoredId storedId, Status status, byte[]? param, byte[]? result, StoredException? storedException, long expires, int expectedEpoch) => inner.SetFunctionState(storedId, status, param, result, storedException, expires, expectedEpoch);
    public Task<bool> SucceedFunction(StoredId storedId, byte[]? result, long timestamp, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState) => inner.SucceedFunction(storedId, result, timestamp, expectedEpoch, effects, messages, complimentaryState);
    public Task<bool> PostponeFunction(StoredId storedId, long postponeUntil, long timestamp, bool ignoreInterrupted, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState) => inner.PostponeFunction(storedId, postponeUntil, timestamp, ignoreInterrupted, expectedEpoch, effects, messages, complimentaryState);
    public Task<bool> FailFunction(StoredId storedId, StoredException storedException, long timestamp, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState) => inner.FailFunction(storedId, storedException, timestamp, expectedEpoch, effects, messages, complimentaryState);
    public Task<bool> SuspendFunction(StoredId storedId, long timestamp, int expectedEpoch, IReadOnlyList<StoredEffect>? effects, IReadOnlyList<StoredMessage>? messages, ComplimentaryState complimentaryState) => inner.SuspendFunction(storedId, timestamp, expectedEpoch, effects, messages, complimentaryState);
    public Task<bool> Interrupt(StoredId storedId, bool onlyIfExecuting) => inner.Interrupt(storedId, onlyIfExecuting);
    public Task Interrupt(IReadOnlyList<StoredId> storedIds) => inner.Interrupt(storedIds);
    public Task<bool?> Interrupted(StoredId storedId) => inner.Interrupted(storedId);
    public Task<StatusAndEpoch?> GetFunctionStatus(StoredId storedId) => inner.GetFunctionStatus(storedId);
    public Task<IReadOnlyList<StatusAndEpochWithId>> GetFunctionsStatus(IEnumerable<StoredId> storedIds) => inner.GetFunctionsStatus(storedIds);
    public Task<StoredFlow?> GetFunction(StoredId storedId) => inner.GetFunction(storedId);
    public Task<IReadOnlyList<StoredInstance>> GetInstances(StoredType storedType, Status status) => inner.GetInstances(storedType, status);
    public Task<IReadOnlyList<StoredInstance>> GetInstances(StoredType storedType) => inner.GetInstances(storedType);
    public Task<bool> DeleteFunction(StoredId storedId) => inner.DeleteFunction(storedId);
}

internal sealed class StartupGatedTimeoutStore(ITimeoutStore inner, DownloadFlowStartupState startup) : ITimeoutStore
{
    public Task Initialize() => inner.Initialize();
    public Task Truncate() => inner.Truncate();
    public Task UpsertTimeout(StoredTimeout timeout, bool overwrite) => inner.UpsertTimeout(timeout,overwrite);
    public Task RemoveTimeout(StoredId id, EffectId timeoutId) => inner.RemoveTimeout(id,timeoutId);
    public Task Remove(StoredId id) => inner.Remove(id);
    public Task<IEnumerable<StoredTimeout>> GetTimeouts(StoredId id) => inner.GetTimeouts(id);
    public Task<IEnumerable<StoredTimeout>> GetTimeouts(long expiresBefore) => startup.IsReady ? inner.GetTimeouts(expiresBefore) : Task.FromResult<IEnumerable<StoredTimeout>>([]);
}
