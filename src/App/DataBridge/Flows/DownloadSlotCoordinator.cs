using Cleipnir.ResilientFunctions.Domain;
using DataBridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Flows;

/// <summary>
/// Singleton that serialises download dispatch in priority order across all Cleipnir flows.
///
/// Before a <see cref="DownloadArchiveFlow"/> dispatches a <c>DownloadVideoCommand</c> it
/// calls <see cref="EnqueueAsync"/> and suspends on <see cref="DownloadSlotGranted"/>. This
/// coordinator keeps one sorted pending list per worker tag and grants the slot to the
/// highest-priority waiter (priority DESC, created_at ASC). When the flow finishes (or fails
/// terminally) it calls <see cref="ReleaseSlotAsync"/> so the next job can proceed.
///
/// Restart recovery: on <see cref="StartAsync"/> the coordinator queries all
/// <see cref="DownloadJobState.DownloadQueued"/> rows from the DB and re-populates its
/// waiting lists, so Cleipnir flows that were suspended at <c>await Message&lt;DownloadSlotGranted&gt;()</c>
/// are re-served after a DataBridge restart. Recovery consults each job's Cleipnir flow first:
/// a flow that died permanently (e.g. killed by a shutdown mid-step) is restarted instead of
/// being re-queued as a corpse that could never consume its grant.
///
/// Self-healing: grants are delivered over <c>flows.SendMessage</c>, which can fail (startup
/// races) or land on a flow that later dies without releasing. Both used to wedge the tag
/// forever, because grants were only re-attempted on enqueue/release. A periodic sweep now
/// (a) retries granting for any tag that has waiters but no active grant, and (b) reclaims or
/// repairs grants whose job is still sitting in <see cref="DownloadJobState.DownloadQueued"/>
/// after <see cref="StalledGrantTimeout"/>.
/// </summary>
public sealed class DownloadSlotCoordinator(
    DownloadArchiveFlows flows,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<DownloadSlotCoordinator> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a granted job may stay in <see cref="DownloadJobState.DownloadQueued"/> before the
    /// sweep treats the grant as stalled and inspects the flow. Generous on purpose: a healthy flow
    /// leaves the queued state within seconds of the grant landing.
    /// </summary>
    private static readonly TimeSpan StalledGrantTimeout = TimeSpan.FromMinutes(2);

    // Per-worker-tag waiting queues, sorted by (priority DESC, createdAt ASC).
    private readonly Dictionary<string, SortedSet<SlotEntry>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    // Tag -> the grant currently holding that tag's slot, with the grant (or last repair) time.
    private readonly Dictionary<string, GrantedSlot> _granted = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _sweepCts = new();
    private Task? _sweepLoop;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DownloadQueuedEntry> pending;
        using (var scope = scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            pending = await repo.GetDownloadQueuedJobsAsync(cancellationToken);
        }

        var recovered = 0;
        foreach (var entry in pending)
        {
            if (!await EnsureFlowIsServableAsync(entry.JobId))
                continue;

            var tag = NormalizeTag(entry.StorageKey);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                AddWaiter(tag, new SlotEntry(entry.JobId, entry.CorrelationId, entry.Priority, entry.CreatedAt));
                recovered++;
            }
            finally
            {
                _lock.Release();
            }
        }

        logger.LogInformation(
            "DownloadSlotCoordinator recovered {Count} of {Total} queued jobs across {Tags} worker tag(s).",
            recovered,
            pending.Count,
            _waiting.Count);

        // Grant any immediately available slots (e.g. no active downloads at start).
        foreach (var tag in _waiting.Keys.ToList())
            await TryGrantNextAsync(tag);

        _sweepLoop = Task.Run(() => SweepLoopAsync(_sweepCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _sweepCts.CancelAsync();
        if (_sweepLoop is not null)
        {
            try
            {
                await _sweepLoop;
            }
            catch
            {
                // Sweep exceptions were already logged.
            }
        }
    }

    public void Dispose() => _sweepCts.Dispose();

    /// <summary>
    /// Called by the flow before it enters the download step. Adds the job to the waiting
    /// list and immediately grants the slot if no other download is active for this tag.
    /// </summary>
    public async Task EnqueueAsync(Guid jobId, Guid correlationId, int priority, string? workerTag, Instant requestedAt)
    {
        var tag = NormalizeTag(workerTag);
        await _lock.WaitAsync();
        try
        {
            // A restarted flow can re-run its enqueue effect for a job the coordinator already
            // tracks (recovered from the DB, or currently granted). A duplicate entry would later
            // produce a second grant for the same job and wedge the slot, so drop it here.
            if (IsTracked(tag, jobId))
            {
                logger.LogDebug(
                    "DownloadSlotCoordinator ignored duplicate enqueue for JobId {JobId} Tag '{Tag}'.",
                    jobId, tag);
                return;
            }

            AddWaiter(tag, new SlotEntry(jobId, correlationId, priority, requestedAt));
            logger.LogDebug(
                "DownloadSlotCoordinator enqueued JobId {JobId} Priority {Priority} Tag '{Tag}'. Queue depth: {Depth}",
                jobId, priority, tag, _waiting[tag].Count);
        }
        finally
        {
            _lock.Release();
        }
        await TryGrantNextAsync(tag);
    }

    /// <summary>
    /// Called by the flow after its download step completes (success or terminal failure).
    /// Releases the slot and grants it to the next highest-priority waiter.
    /// </summary>
    public async Task ReleaseSlotAsync(string? workerTag)
    {
        var tag = NormalizeTag(workerTag);
        await _lock.WaitAsync();
        try
        {
            _granted.Remove(tag);
            logger.LogDebug("DownloadSlotCoordinator released slot for Tag '{Tag}'.", tag);
        }
        finally
        {
            _lock.Release();
        }
        await TryGrantNextAsync(tag);
    }

    /// <summary>
    /// Updates a waiting job's priority. No-op if the job is not in the waiting list
    /// (already downloading or finished). Re-evaluates the grant order after the update.
    /// </summary>
    public async Task UpdatePriorityAsync(Guid jobId, int newPriority, string? workerTag)
    {
        var tag = NormalizeTag(workerTag);
        var changed = false;
        await _lock.WaitAsync();
        try
        {
            if (_waiting.TryGetValue(tag, out var set))
            {
                var existing = set.FirstOrDefault(e => e.JobId == jobId);
                if (existing != default)
                {
                    set.Remove(existing);
                    set.Add(existing with { Priority = newPriority });
                    changed = true;
                    logger.LogDebug(
                        "DownloadSlotCoordinator updated priority for JobId {JobId} → {Priority} Tag '{Tag}'.",
                        jobId, newPriority, tag);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
        if (changed)
            await TryGrantNextAsync(tag);
    }

    /// <summary>
    /// Removes a waiting job from the priority queue. No-op if the job has already been granted
    /// a slot or is not queued for this tag.
    /// </summary>
    public async Task<bool> CancelQueuedAsync(Guid jobId, string? workerTag)
    {
        var tag = NormalizeTag(workerTag);
        await _lock.WaitAsync();
        try
        {
            if (!_waiting.TryGetValue(tag, out var set))
                return false;

            var existing = set.FirstOrDefault(e => e.JobId == jobId);
            if (existing == default)
                return false;

            set.Remove(existing);
            logger.LogDebug(
                "DownloadSlotCoordinator removed cancelled JobId {JobId} from Tag '{Tag}'. Queue depth: {Depth}",
                jobId,
                tag,
                set.Count);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task TryGrantNextAsync(string tag)
    {
        SlotEntry? toGrant = null;
        await _lock.WaitAsync();
        try
        {
            if (_granted.ContainsKey(tag))
                return;
            if (!_waiting.TryGetValue(tag, out var set) || set.Count == 0)
                return;
            toGrant = set.Min;
            set.Remove(toGrant.Value);
            _granted[tag] = new GrantedSlot(toGrant.Value, Environment.TickCount64);
        }
        finally
        {
            _lock.Release();
        }

        if (toGrant is null)
            return;

        var granted = toGrant.Value;
        logger.LogInformation(
            "DownloadSlotCoordinator granting slot to JobId {JobId} Priority {Priority} Tag '{Tag}'.",
            granted.JobId, granted.Priority, tag);
        if (!await TrySendGrantAsync(granted, tag))
        {
            // Put it back so the sweep (or the next enqueue/release) retries the grant.
            await _lock.WaitAsync();
            try
            {
                _granted.Remove(tag);
                AddWaiter(tag, granted);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private async Task<bool> TrySendGrantAsync(SlotEntry granted, string tag)
    {
        try
        {
            await flows.SendMessage(granted.JobId.ToString("N"), new DownloadSlotGranted
            {
                JobId = granted.JobId,
                CorrelationId = granted.CorrelationId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"job/{granted.JobId:N}/slot-granted",
                OccurredAt = clock.GetCurrentInstant(),
                WorkerTag = tag
            });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DownloadSlotCoordinator failed to send DownloadSlotGranted to flow for JobId {JobId}; will retry.",
                granted.JobId);
            return false;
        }
    }

    // ── Self-healing sweep ────────────────────────────────────────────────────────────

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await SweepOnceAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Download slot sweep failed; retrying on the next interval.");
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        List<string> grantable;
        List<(string Tag, SlotEntry Entry)> stalled;
        await _lock.WaitAsync(ct);
        try
        {
            grantable = _waiting
                .Where(kvp => kvp.Value.Count > 0 && !_granted.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Key)
                .ToList();
            var now = Environment.TickCount64;
            stalled = _granted
                .Where(kvp => now - kvp.Value.GrantedAtTicks >= (long)StalledGrantTimeout.TotalMilliseconds)
                .Select(kvp => (kvp.Key, kvp.Value.Entry))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }

        // Tags whose waiters never got a grant (e.g. the grant send failed at startup).
        foreach (var tag in grantable)
            await TryGrantNextAsync(tag);

        foreach (var (tag, entry) in stalled)
            await RepairStalledGrantAsync(tag, entry, ct);
    }

    /// <summary>
    /// A grant is stalled when its slot has been held past <see cref="StalledGrantTimeout"/>.
    /// Healthy long downloads are left alone (the job has moved past <see cref="DownloadJobState.DownloadQueued"/>
    /// and its flow is alive); everything else is repaired or reclaimed so one dead flow can
    /// never wedge the whole tag again.
    /// </summary>
    private async Task RepairStalledGrantAsync(string tag, SlotEntry entry, CancellationToken ct)
    {
        DownloadJobState? state;
        using (var scope = scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            (state, _) = await repo.GetJobStateAndStorageKeyAsync(entry.JobId, ct);
        }

        var panel = await flows.ControlPanel(entry.JobId.ToString("N"));

        if (panel is null || state is null)
        {
            logger.LogError(
                "DownloadSlotCoordinator reclaiming slot for JobId {JobId} Tag '{Tag}': {Reason}. The job needs manual attention.",
                entry.JobId, tag, panel is null ? "its Cleipnir flow no longer exists" : "its job row no longer exists");
            await ReclaimAsync(tag, entry.JobId);
            return;
        }

        switch (panel.Status)
        {
            case Status.Failed:
                // The flow died (e.g. killed by a shutdown mid-step). Restart it: completed
                // effects replay from the store, and the grant message already delivered to its
                // message store is consumed when it reaches the slot gate again.
                logger.LogWarning(
                    "DownloadSlotCoordinator restarting failed flow for granted JobId {JobId} Tag '{Tag}' (job state {State}).",
                    entry.JobId, tag, state);
                await panel.ScheduleRestart(clearFailures: true);
                await RefreshGrantTimestampAsync(tag, entry.JobId);
                break;

            case Status.Succeeded:
                // The flow is done but the release never reached this coordinator instance
                // (e.g. it happened before a restart). The slot is leaked — reclaim it.
                logger.LogWarning(
                    "DownloadSlotCoordinator reclaiming leaked slot for JobId {JobId} Tag '{Tag}': flow already succeeded (job state {State}).",
                    entry.JobId, tag, state);
                await ReclaimAsync(tag, entry.JobId);
                break;

            case Status.Suspended when state == DownloadJobState.DownloadQueued:
                // The flow is parked at the slot gate but never consumed the grant — the
                // message or its wake-up interrupt was lost. Deliver it again; duplicates are
                // harmless because the gate consumes exactly one grant.
                logger.LogWarning(
                    "DownloadSlotCoordinator re-sending lost slot grant to JobId {JobId} Tag '{Tag}'.",
                    entry.JobId, tag);
                await TrySendGrantAsync(entry, tag);
                await RefreshGrantTimestampAsync(tag, entry.JobId);
                break;

            default:
                // Executing/Postponed, or suspended at a later await (normal for long
                // downloads): the flow is alive and will release the slot itself.
                await RefreshGrantTimestampAsync(tag, entry.JobId);
                break;
        }
    }

    private async Task ReclaimAsync(string tag, Guid jobId)
    {
        await _lock.WaitAsync();
        try
        {
            // Only reclaim if the slot is still held by the job we inspected.
            if (_granted.TryGetValue(tag, out var current) && current.Entry.JobId == jobId)
                _granted.Remove(tag);
            else
                return;
        }
        finally
        {
            _lock.Release();
        }
        await TryGrantNextAsync(tag);
    }

    private async Task RefreshGrantTimestampAsync(string tag, Guid jobId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_granted.TryGetValue(tag, out var current) && current.Entry.JobId == jobId)
                _granted[tag] = current with { GrantedAtTicks = Environment.TickCount64 };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Recovery guard: returns false when the job's flow cannot be served a grant (missing), and
    /// restarts flows that died permanently so they can re-reach the slot gate.
    /// </summary>
    private async Task<bool> EnsureFlowIsServableAsync(Guid jobId)
    {
        try
        {
            var panel = await flows.ControlPanel(jobId.ToString("N"));
            if (panel is null)
            {
                logger.LogError(
                    "DownloadSlotCoordinator skipping queued JobId {JobId}: its Cleipnir flow no longer exists. The job needs manual attention.",
                    jobId);
                return false;
            }

            if (panel.Status == Status.Failed)
            {
                logger.LogWarning(
                    "DownloadSlotCoordinator restarting failed flow for queued JobId {JobId} before re-queueing it.",
                    jobId);
                await panel.ScheduleRestart(clearFailures: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Recovery must not die on one broken flow; queue it and let the sweep sort it out.
            logger.LogWarning(ex,
                "DownloadSlotCoordinator could not inspect the flow for queued JobId {JobId}; re-queueing it anyway.",
                jobId);
            return true;
        }
    }

    private void AddWaiter(string tag, SlotEntry entry)
    {
        if (!_waiting.TryGetValue(tag, out var set))
            _waiting[tag] = set = new SortedSet<SlotEntry>(SlotEntryComparer.Instance);
        set.Add(entry);
    }

    /// <summary>Whether the job is already waiting or currently granted for this tag. Caller holds the lock.</summary>
    private bool IsTracked(string tag, Guid jobId)
        => (_granted.TryGetValue(tag, out var granted) && granted.Entry.JobId == jobId)
           || (_waiting.TryGetValue(tag, out var set) && set.Any(e => e.JobId == jobId));

    private static string NormalizeTag(string? tag)
        => string.IsNullOrWhiteSpace(tag) ? string.Empty : tag.Trim();

    private sealed record GrantedSlot(SlotEntry Entry, long GrantedAtTicks);

    private record struct SlotEntry(Guid JobId, Guid CorrelationId, int Priority, Instant CreatedAt);

    private sealed class SlotEntryComparer : IComparer<SlotEntry>
    {
        public static readonly SlotEntryComparer Instance = new();

        public int Compare(SlotEntry x, SlotEntry y)
        {
            // Higher priority first.
            var c = y.Priority.CompareTo(x.Priority);
            if (c != 0) return c;
            // Earlier creation first among equal priorities.
            c = x.CreatedAt.CompareTo(y.CreatedAt);
            if (c != 0) return c;
            // Tie-break on JobId so equal-priority, same-instant jobs are stable.
            return x.JobId.CompareTo(y.JobId);
        }
    }
}
