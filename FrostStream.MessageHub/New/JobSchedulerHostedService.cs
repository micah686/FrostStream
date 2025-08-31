using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FrostStream.Shared; // for WireMessage, MessageHeader, ControlCommand, ServiceType, PayloadType
using FrostStream.Shared.Models; // for Job model and JobStatus enum
using LiteDB;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.MessageHub.New
{
    /// <summary>
    /// Pure job scheduling component (no sockets, no threads).
    /// 
    /// IMPORTANT:
    ///  - All methods MUST be called from the broker's NetMQ thread (e.g., inside its poller callbacks or NetMQTimer).
    ///  - This class never touches NetMQ sockets; it relies on a delegate provided by the broker to perform sends.
    ///  - "Presence pruning" is performed by ServiceRegistryCleanup; this scheduler only reconciles its local worker map
    ///    with the registry snapshot and requeues jobs when workers disappear.
    /// </summary>
    public sealed class JobScheduler : IDisposable
    {
        private readonly ServiceRegistry _registry;
        private readonly ILogger<JobScheduler> _log;
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<Job> _jobs;

        /// <summary>
        /// Minimal internal worker state for scheduling.
        /// We intentionally do NOT cache ROUTER identities here (the broker resolves them fresh from ServiceRegistry).
        /// </summary>
        private sealed class WorkerState
        {
            public Guid? CurrentJob { get; set; }
        }

        /// <summary>
        /// In-memory view of known workers (by friendly name / workerId) and their current job.
        /// This is a "secondary" bookkeeping list; ServiceRegistry remains the source of truth for presence.
        /// </summary>
        private readonly ConcurrentDictionary<string, WorkerState> _workers = new(StringComparer.OrdinalIgnoreCase);

        public JobScheduler(ServiceRegistry registry, string dbPath, ILogger<JobScheduler> log)
        {
            _registry = registry;
            _log = log;

            _db = new LiteDatabase(dbPath);
            _jobs = _db.GetCollection<Job>("jobs");
            _jobs.EnsureIndex(j => j.JobGuid);

            ResetOrphanedJobs();
        }

        public void Dispose()
        {
            try { _db?.Dispose(); }
            catch (Exception ex) { _log.LogError(ex, "Error disposing JobScheduler database."); }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Startup recovery
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// On process start, any job stuck as InProgress is reset to Pending so it can be reassigned.
        /// </summary>
        private void ResetOrphanedJobs()
        {
            var inProgress = _jobs.Find(j => j.Status == JobStatus.InProgress).ToList();
            foreach (var job in inProgress)
            {
                job.Status = JobStatus.Pending;
                job.AssignedAgent = null;
                job.RetryCount = 0;
                job.NextAttemptAt = DateTime.UtcNow;
                _jobs.Update(job);
                _log.LogWarning("Recovered orphaned job {JobId} -> Pending.", job.JobGuid);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Worker lifecycle hooks (called by broker upon messages like Ready / Heartbeat)
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called when a worker announces Ready (or similar). Marks/creates it as idle in the local map.
        /// </summary>
        public void RegisterWorker(string workerId)
        {
            _workers.AddOrUpdate(workerId,
                _ => new WorkerState { CurrentJob = null },
                (_, existing) => { existing.CurrentJob = null; return existing; });

            _log.LogInformation("Worker {WorkerId} registered (idle).", workerId);
        }

        /// <summary>
        /// Optional no-op hook if you want to note heartbeat activity locally.
        /// Presence/LastSeenUtc is tracked by ServiceRegistry via broker.Upsert on every inbound message.
        /// </summary>
        public void RecordHeartbeat(string workerId)
        {
            if (!_workers.ContainsKey(workerId))
                _workers[workerId] = new WorkerState { CurrentJob = null };
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Main entry: assign an incoming job request to an idle worker or queue as Pending
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Assign an incoming job request to an idle worker if available; otherwise persist as Pending.
        /// Must be called on the broker's NetMQ thread. The broker supplies a thread-safe send delegate.
        /// </summary>
        /// <param name="jobMsg">The inbound job (e.g., from WebApi) to dispatch to a worker.</param>
        /// <param name="sendToWorker">
        /// Delegate implemented by the broker that resolves identity and sends on the broker's NetMQ thread.
        /// Returns true if the send was attempted successfully (no HostUnreachableException); false otherwise.
        /// </param>
        public void AssignJob(WireMessage jobMsg, Func<string, WireMessage, bool> sendToWorker)
        {
            // Choose an idle worker using LRU based on registry.LastSeenUtc.
            var idle = _workers.Where(kv => kv.Value.CurrentJob == null)
                               .OrderBy(kv => GetLastSeenUtc(kv.Key))
                               .ToList();

            if (!idle.Any())
            {
                // No capacity -> persist as Pending for later
                var pending = new Job
                {
                    JobGuid = jobMsg.Header.JobId,
                    Payload = Convert.ToBase64String(jobMsg.Payload ?? Array.Empty<byte>()),
                    PayloadType = jobMsg.Header.PayloadType,
                    Status = JobStatus.Pending,
                    AssignedAgent = null,
                    CreatedAt = DateTime.UtcNow,
                    NextAttemptAt = DateTime.UtcNow
                };
                _jobs.Insert(pending);
                _log.LogWarning("No idle workers. Queued job {JobId} -> Pending.", pending.JobGuid);
                return;
            }

            var workerId = idle.First().Key;

            // Persist as InProgress with assignment
            var job = new Job
            {
                JobGuid = jobMsg.Header.JobId,
                Payload = Convert.ToBase64String(jobMsg.Payload ?? Array.Empty<byte>()),
                PayloadType = jobMsg.Header.PayloadType,
                Status = JobStatus.InProgress,
                AssignedAgent = workerId,
                CreatedAt = DateTime.UtcNow,
                LastTriedAt = DateTime.UtcNow,
                RetryCount = 0
            };
            _jobs.Insert(job);
            _workers[workerId].CurrentJob = job.JobGuid;

            // Build dispatch message preserving original payload type when possible
            var hdr = new MessageHeader
            {
                Command = ControlCommand.JobDispatch,
                ServiceName = "Broker",
                Source = ServiceType.Broker,
                Target = ServiceType.Worker,
                PayloadType = jobMsg.Header.PayloadType, // keep original (Json/String/RawBytes)
                RequiresAck = false,
                CorrelationId = jobMsg.Header.CorrelationId != Guid.Empty ? jobMsg.Header.CorrelationId : Guid.NewGuid(),
                CausationId = jobMsg.Header.MessageId,
                JobId = job.JobGuid,
                WorkerId = workerId
            };
            var dispatch = new WireMessage(hdr, jobMsg.Payload);

            if (!sendToWorker(workerId, dispatch))
            {
                // Could not send → requeue with backoff and free the worker.
                // DO NOT remove from ServiceRegistry here; leave it to ServiceRegistryCleanup.
                _log.LogWarning("Send failed to {WorkerId}; requeue job {JobId}.", workerId, job.JobGuid);

                job.Status = JobStatus.Pending;
                job.AssignedAgent = null;
                job.RetryCount = 1;
                job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, job.RetryCount) * 5);
                _jobs.Update(job);

                _workers[workerId].CurrentJob = null;
            }
            else
            {
                _log.LogInformation("Dispatched job {JobId} -> {WorkerId}.", job.JobGuid, workerId);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Job lifecycle hooks
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mark a job completed and free the worker for the next job.
        /// </summary>
        public void MarkJobDone(Guid jobId, string workerId)
        {
            var job = _jobs.FindOne(j => j.JobGuid == jobId);
            if (job != null)
            {
                job.Status = JobStatus.Done;
                _jobs.Update(job);
                _log.LogInformation("Job {JobId} Done by {WorkerId}.", jobId, workerId);
            }

            if (_workers.TryGetValue(workerId, out var ws))
                ws.CurrentJob = null;
        }

        /// <summary>
        /// Cancel a job (mark Failed) and free any worker currently assigned to it.
        /// </summary>
        public void CancelJob(Guid jobId)
        {
            var job = _jobs.FindOne(j => j.JobGuid == jobId);
            if (job != null)
            {
                job.Status = JobStatus.Failed;
                _jobs.Update(job);
                _log.LogInformation("Job {JobId} Cancelled -> Failed.", jobId);
            }

            var holder = _workers.FirstOrDefault(kv => kv.Value.CurrentJob == jobId);
            if (!string.IsNullOrEmpty(holder.Key))
                _workers[holder.Key].CurrentJob = null;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Periodic maintenance called by the broker via NetMQTimer (on the NetMQ thread)
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reconcile the local worker map against ServiceRegistry membership.
        /// If a worker has been evicted from the registry (by ServiceRegistryCleanup),
        /// we requeue its job (with backoff) or mark it failed if retries are exhausted.
        /// </summary>
        public void ReconcileRegistryAndRequeue()
        {
            // Snapshot of current active workers in the registry
            var active = _registry.GetAll()
                                  .Where(r => r.Type == ServiceType.Worker)
                                  .Select(r => r.ServiceName)
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in _workers.ToArray())
            {
                var workerId = kv.Key;

                // If a worker tracked locally is no longer present in the registry → treat as dead
                if (!active.Contains(workerId))
                {
                    if (kv.Value.CurrentJob is Guid jobId)
                    {
                        var job = _jobs.FindOne(j => j.JobGuid == jobId && j.Status == JobStatus.InProgress);
                        if (job != null)
                        {
                            job.RetryCount++;
                            if (job.RetryCount > job.MaxRetries)
                            {
                                job.Status = JobStatus.Failed;
                                _log.LogWarning("Job {JobId} FAILED after {RetryCount} attempts (worker {WorkerId} evicted).",
                                                job.JobGuid, job.RetryCount, workerId);
                            }
                            else
                            {
                                job.Status = JobStatus.Pending;
                                job.AssignedAgent = null;
                                job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, job.RetryCount) * 5);
                                _log.LogInformation("Requeued job {JobId} from evicted worker {WorkerId} (Retry #{RetryCount} @ {NextAttemptAt:O}).",
                                                    job.JobGuid, workerId, job.RetryCount, job.NextAttemptAt);
                            }
                            _jobs.Update(job);
                        }
                    }

                    // Drop from local map. Do NOT remove from ServiceRegistry (cleanup already did).
                    _workers.TryRemove(workerId, out _);
                }
            }
        }

        /// <summary>
        /// Attempt to dispatch due Pending jobs to currently idle workers.
        /// Must be called on the broker's NetMQ thread (e.g., from a NetMQTimer tick).
        /// </summary>
        public void RequeueDueJobs(Func<string, WireMessage, bool> sendToWorker)
        {
            var now = DateTime.UtcNow;

            // 1) Fetch pending jobs that are due (NextAttemptAt <= now)
            var due = _jobs.Find(j => j.Status == JobStatus.Pending &&
                                      (j.NextAttemptAt == null || j.NextAttemptAt <= now))
                           .ToList();
            if (!due.Any()) return;

            // 2) Build idle worker list (LRU by last seen)
            var idle = _workers.Where(kv => kv.Value.CurrentJob == null)
                               .OrderBy(kv => GetLastSeenUtc(kv.Key))
                               .ToList();
            if (!idle.Any()) return;

            // 3) Try to assign one job per idle worker
            foreach (var job in due)
            {
                if (!idle.Any()) break;

                var workerId = idle[0].Key;
                idle.RemoveAt(0);

                // Build dispatch wire message; when persisted, payload is base64, so convert back to bytes.
                var hdr = new MessageHeader
                {
                    Command = ControlCommand.JobDispatch,
                    ServiceName = "Broker",
                    Source = ServiceType.Broker,
                    Target = ServiceType.Worker,
                    PayloadType = job.PayloadType, // use original payload type
                    RequiresAck = false,
                    CorrelationId = Guid.NewGuid(),
                    JobId = job.JobGuid,
                    WorkerId = workerId
                };
                var payload = Convert.FromBase64String(job.Payload);
                var wire = new WireMessage(hdr, payload);

                // Mark InProgress and attempt to send
                job.Status = JobStatus.InProgress;
                job.AssignedAgent = workerId;
                job.LastTriedAt = DateTime.UtcNow;
                job.NextAttemptAt = null;
                _jobs.Update(job);

                _workers[workerId].CurrentJob = job.JobGuid;

                if (!sendToWorker(workerId, wire))
                {
                    // Send failed → back to Pending with exponential backoff. Do NOT touch ServiceRegistry.
                    _log.LogWarning("Send failed to {WorkerId}; requeue job {JobId}.", workerId, job.JobGuid);

                    job.Status = JobStatus.Pending;
                    job.AssignedAgent = null;
                    job.RetryCount++;
                    job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, job.RetryCount) * 5);
                    _jobs.Update(job);

                    _workers[workerId].CurrentJob = null;
                }
                else
                {
                    _log.LogInformation("Re-dispatched job {JobId} -> {WorkerId}.", job.JobGuid, workerId);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Get the registry's LastSeenUtc for a worker, or DateTime.MinValue if the worker is unknown.
        /// Used to LRU-order idle workers for fairness.
        /// </summary>
        private DateTime GetLastSeenUtc(string workerId)
        {
            return _registry.TryGet(workerId, out var rec) ? rec.LastSeenUtc : DateTime.MinValue;
        }
    }
}
