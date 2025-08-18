using System.Collections.Concurrent;
using FrostStream.Shared;
using FrostStream.Shared.Models;
using LiteDB;
using NetMQ;
using NetMQ.Sockets;

namespace FrostStream.MessageHub;

public class JobScheduler
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Job> _jobs;
    private readonly ConcurrentDictionary<string, WorkerState> _workers = new();

    private class WorkerState
    {
        public byte[] Identity { get; set; }
        public DateTime LastSeen { get; set; }
        public Guid? CurrentJob { get; set; }
    }

    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);

    public JobScheduler(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
        _jobs = _db.GetCollection<Job>("jobs");
        // ensure index on JobGuid for fast lookups
        _jobs.EnsureIndex(x => x.JobGuid);
    }

    public void RegisterWorker(byte[] identity, string workerId)
    {
        var msg = string.Empty;
        if(_workers.ContainsKey(workerId))
        {
            msg = $"Worker {workerId} ready for new jobs.";
        }
        else
        {
            msg = $"New worker {workerId} registered.";
        }

        _workers[workerId] = new WorkerState
        {
            Identity = identity,
            LastSeen = DateTime.UtcNow,
            CurrentJob = null
        };
        Console.WriteLine(msg);
    }

    

    public void HeartbeatOrRegister(byte[] identity, string workerId)
    {
        if (_workers.TryGetValue(workerId, out var state))
        {
            // Worker already tracked → update heartbeat
            state.LastSeen = DateTime.UtcNow;
            // don’t reset CurrentJob here, that’s only cleared on JobDone
            Console.WriteLine($"Heartbeat received from worker {workerId}");
        }
        else
        {
            // New worker or broker lost it → re-register
            _workers[workerId] = new WorkerState
            {
                Identity = identity,
                LastSeen = DateTime.UtcNow,
                CurrentJob = null
            };
            Console.WriteLine($"Worker {workerId} ready for new jobs via Heartbeat");
        }
    }


    /// <summary>
    /// Assigns a new job request (WireMessage from WebAPI) to an idle worker if available.
    /// Persists the job to LiteDB with the same GUID as in the WireMessage.
    /// </summary>
    public void AssignJob(WireMessage jobMsg, RouterSocket workers)
    {
        // choose an idle worker using LRU (oldest LastSeen)
        var worker = _workers
            .Where(kv => kv.Value.CurrentJob == null) // only idle workers
            .OrderBy(kv => kv.Value.LastSeen)
            .FirstOrDefault();

        if (worker.Key == null)
        {
            Console.WriteLine("No idle workers available to assign job.");
            // persist job as Pending for later requeue
            var pendingJob = new Job
            {
                JobGuid = jobMsg.JobId,
                Payload = Convert.ToBase64String(jobMsg.Payload),
                Status = JobStatus.Pending,
                AssignedAgent = null,
                CreatedAt = DateTime.UtcNow
            };
            _jobs.Insert(pendingJob);
            return;
        }

        // persist as InProgress with assigned agent
        var job = new Job
        {
            JobGuid = jobMsg.JobId,
            Payload = Convert.ToBase64String(jobMsg.Payload),
            Status = JobStatus.InProgress,
            AssignedAgent = worker.Key,
            CreatedAt = DateTime.UtcNow,
            LastTriedAt = DateTime.UtcNow
        };
        _jobs.Insert(job);

        // mark worker state
        _workers[worker.Key].CurrentJob = job.JobGuid;

        // send wire message to chosen worker identity
        workers.SendMultipartMessage(jobMsg.ToNetMQMessage(worker.Value.Identity));
        Console.WriteLine($"Dispatched job {job.JobGuid} to worker {worker.Key}");
    }

    /// <summary>
    /// Helper to send a persisted Job to a specific worker and mark DB state.
    /// </summary>
    private void AssignJobToWorker(Job job, string workerKey, byte[] workerIdentity, RouterSocket workers)
    {
        // build WireMessage based on persisted job
        var wire = new WireMessage(
            Command: ControlCommand.JobDispatch,
            JobId: job.JobGuid,
            WorkerId: workerKey,
            Payload: Convert.FromBase64String(job.Payload),
            CorrelationId: Guid.NewGuid()
        );

        job.Status = JobStatus.InProgress;
        job.AssignedAgent = workerKey;
        job.LastTriedAt = DateTime.UtcNow;
        _jobs.Update(job);

        _workers[workerKey].CurrentJob = job.JobGuid;

        workers.SendMultipartMessage(wire.ToNetMQMessage(workerIdentity));
        Console.WriteLine($"Re-dispatched persisted job {job.JobGuid} to worker {workerKey}");
    }

    public void CancelJob(Guid jobId)
    {
        var job = _jobs.FindOne(x => x.JobGuid == jobId);
        if (job != null)
        {
            job.Status = JobStatus.Failed;
            _jobs.Update(job);
            Console.WriteLine($"Job {jobId} cancelled.");
        }
    }
    
    public void MarkJobDone(Guid jobId, string workerId)
    {
        var job = _jobs.FindOne(x => x.JobGuid == jobId);
        if (job != null)
        {
            job.Status = JobStatus.Done;
            _jobs.Update(job);
            Console.WriteLine($"Job {jobId} marked as Done by worker {workerId}");
        }
    }

    public bool TryGetWorkerIdentity(string workerId, out byte[] identity)
    {
        if (_workers.TryGetValue(workerId, out var state))
        {
            identity = state.Identity;
            return true;
        }
        identity = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// Called periodically to detect dead workers and requeue their in-progress jobs.
    /// </summary>
    public void CheckHeartbeats(RouterSocket workers)
    {
        var cutoff = DateTime.UtcNow - _heartbeatTimeout;
        foreach (var kv in _workers.ToArray())
        {
            if (kv.Value.LastSeen < cutoff)
            {
                Console.WriteLine($"Worker {kv.Key} timed out. Requeuing its job...");
                if (kv.Value.CurrentJob != null)
                {
                    var job = _jobs.FindOne(x => x.JobGuid == kv.Value.CurrentJob && x.Status == JobStatus.InProgress);
                    if (job != null)
                    {
                        job.RetryCount++;
                        if (job.RetryCount > job.MaxRetries)
                        {
                            job.Status = JobStatus.Failed;
                            Console.WriteLine($"Job {job.JobGuid} marked as FAILED after {job.RetryCount} attempts.");
                        }
                        else
                        {
                            job.Status = JobStatus.Pending;
                            job.AssignedAgent = null;
                            job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Pow(2, job.RetryCount) * 5); 
                            Console.WriteLine($"Job {job.JobGuid} requeued with backoff. Retry #{job.RetryCount}, next at {job.NextAttemptAt}.");
                        }
                        _jobs.Update(job);
                    }
                }
                _workers.TryRemove(kv.Key, out _);
            }
        }
    }


    /// <summary>
    /// Attempt to requeue jobs stored in the DB with status Pending.
    /// This tries to assign pending jobs to idle workers using LRU order.
    /// If no workers are idle, jobs remain Pending.
    /// </summary>
    public void RequeueJobs(RouterSocket workers)
    {
        var now = DateTime.UtcNow;
        //used to be just JobStatus.Pending, but now  doing any job that wasn't marked as Done
        var pendingJobs = _jobs.Find(x => x.Status != JobStatus.Done && (x.NextAttemptAt == null || x.NextAttemptAt <= now)).ToList();
        if (!pendingJobs.Any())
        {
            Console.WriteLine("No pending jobs ready for requeue.");
            return;
        }

        Console.WriteLine($"Attempting to requeue {pendingJobs.Count} job(s).");

        var idleWorkers = _workers
            .Where(kv => kv.Value.CurrentJob == null)
            .OrderBy(kv => kv.Value.LastSeen)
            .ToList();

        foreach (var job in pendingJobs)
        {
            if (!idleWorkers.Any())
            {
                Console.WriteLine("No idle workers left; stopping requeue attempts.");
                break;
            }

            var selected = idleWorkers.First();
            AssignJobToWorker(job, selected.Key, selected.Value.Identity, workers);
            idleWorkers.RemoveAt(0);
        }
    }
}