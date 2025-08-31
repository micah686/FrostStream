using System;
using System.IO;
using System.Reflection;
using FrostStream.MessageHub.New;
using FrostStream.Shared;
using FrostStream.Shared.Models;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class JobSchedulerTests
{
    [Fact]
    public void JobEventuallyMarkedFailedAfterSendFailures()
    {
        var registry = new ServiceRegistry();
        var logger = NullLogger<JobScheduler>.Instance;
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var scheduler = new JobScheduler(registry, dbPath, logger);
        scheduler.RegisterWorker("worker-1");

        var jobsField = typeof(JobScheduler).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance);
        var jobs = (ILiteCollection<Job>)jobsField!.GetValue(scheduler)!;

        var jobId = Guid.NewGuid();
        var header = new MessageHeader
        {
            Command = ControlCommand.JobDispatch,
            ServiceName = "test",
            Source = ServiceType.WebApi,
            Target = ServiceType.Broker,
            PayloadType = PayloadType.RawBytes,
            RequiresAck = false,
            JobId = jobId
        };
        var wire = new WireMessage(header, new byte[] { 1 });

        Func<string, WireMessage, bool> failSend = (_, __) => false;

        scheduler.AssignJob(wire, failSend);

        for (int i = 0; i < 3; i++)
        {
            var job = jobs.FindOne(j => j.JobGuid == jobId);
            job.NextAttemptAt = DateTime.UtcNow;
            jobs.Update(job);
            scheduler.RequeueDueJobs(failSend);
        }

        var finalJob = jobs.FindOne(j => j.JobGuid == jobId);
        Assert.Equal(JobStatus.Failed, finalJob.Status);

        scheduler.Dispose();
    }
}
