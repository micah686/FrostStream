using Cleipnir.Flows;
using Cleipnir.Flows.AspNet;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Storage;
using DataBridge;
using DataBridge.Data;
using DataBridge.Flows;
using DataBridge.Messaging;
using DataBridge.Persistence;
using DataBridge.Persistence.Workflows;
using FrostStream.ApplicationContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using static UnitTests.DataBridge.CoreRepositoryPersistenceTests;

namespace UnitTests.DataBridge;

public sealed class DownloadRestartPersistenceTests
{
    private static readonly Instant Now=Instant.FromUtc(2026,10,3,12,0);
    private static DownloadRequested Request() => new() { JobId=Guid.NewGuid(),CorrelationId=Guid.NewGuid(),MessageId=Guid.NewGuid(),OperationKey="initial",OccurredAt=Now-Duration.FromHours(1),SourceUrl="https://test",StorageKey="default",RequestedBy="alice" };
    private static DownloadFlowV2Repository Repo(DataBridgeDbContext db) => new(db,new FixedClock(Now),NullDownloadJobStateNotifier.Instance);
    private static DownloadExecutionIdentity Execution(DownloadRunRequest run) => new() { JobId=run.Request.JobId,RunId=run.RunId,Stage=DownloadStage.Metadata,Attempt=1,DispatchId=Guid.NewGuid(),CorrelationId=run.Request.CorrelationId };
    private static async Task BothRuntime(Func<Fixture,Task> test)
    {
        foreach(var postgres in string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES")) ? new[]{false}:new[]{false,true})
        {
            await using var f=await Fixture.Create(postgres,Configure);
            await test(f);
        }
    }
    private static void Configure(IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IClock>(new FixedClock(Now));
        builder.Services.AddSingleton<DownloadFlowStartupState>();
        builder.Services.AddSingleton<IDownloadJobStateNotifier>(NullDownloadJobStateNotifier.Instance);
        builder.Services.AddScoped<IDownloadFlowV2Repository,DownloadFlowV2Repository>();
        builder.Services.AddScoped<IDownloadJobsRepository,DownloadJobsRepository>();
        builder.Services.AddSingleton(Substitute.For<IDurableJobPublisher>());
        builder.Services.AddSingleton(Substitute.For<IMessageBus>());
        builder.Services.AddSingleton(Substitute.For<IDurableJobConsumer>());
        builder.Services.AddFlows(c=>c.UsePersistenceStore(PersistenceOptions.FromConfiguration(builder.Configuration,builder.Environment.ContentRootPath),
                builder.Configuration["ConnectionStrings:froststreamdb"] ?? "")
            .WithOptions(new Options(serializer:new NodaTimeFlowSerializer(),leaseLength:TimeSpan.FromMilliseconds(100),watchdogCheckFrequency:TimeSpan.FromMilliseconds(10),delayStartup:TimeSpan.Zero))
            .RegisterFlow<DownloadJobV2Flow,DownloadJobV2Flows>()
            .RegisterFlow<DownloadGroupV2Flow,DownloadGroupV2Flows>());
        builder.Services.AddSingleton<DownloadFlowStartupService>();
    }

    [Test]
    public Task Startup_Settles_Queued_Active_Attempts_Groups_And_Leases_And_Advances_A_Durable_Fence() => Both(async f=>
    {
        var repo=Repo(f.Db); var requests=Enumerable.Range(0,5).Select(_=>Request()).ToArray();
        var runs=new List<DownloadRunRequest>(); var executions=new List<DownloadExecutionIdentity>();
        foreach(var request in requests)
        {
            var run=(await repo.CreateInitialRunAsync(request,true)).ShouldNotBeNull(); runs.Add(run);
            var execution=Execution(run); executions.Add(execution);
            (await repo.BeginStageAttemptAsync(execution,"attempt")).ShouldBeTrue();
            (await repo.TryAcquireLeaseAsync(new() { Execution=execution,OccurredAt=Now,WorkerInstanceId="worker" })).Granted.ShouldBeTrue();
        }
        await f.Db.DownloadJobs.Where(x=>x.JobId==requests[0].JobId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,DownloadJobStatus.Queued));
        await f.Db.DownloadJobRuns.Where(x=>x.RunId==runs[0].RunId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,DownloadJobStatus.Queued));
        await f.Db.DownloadJobs.Where(x=>x.JobId==requests[2].JobId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,DownloadJobStatus.Stopping));
        await f.Db.DownloadJobs.Where(x=>x.JobId==requests[3].JobId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,DownloadJobStatus.Compensating));
        (await repo.CompleteRunAsync(requests[4].JobId,runs[4].RunId,false)).ShouldBeTrue();
        var count=await f.Db.DownloadJobRuns.CountAsync();
        var result=await repo.ReconcileForStartupAsync();
        result.StoppedQueuedJobs.ShouldBe(1); result.FailedActiveJobs.ShouldBe(3); result.ExpiredLeases.ShouldBe(5);
        result.GenerationStartedAt.ShouldBe(Now+Duration.FromTicks(10)); (await f.Db.DownloadJobRuns.CountAsync()).ShouldBe(count);
        var jobs=await f.Db.DownloadJobs.AsNoTracking().ToDictionaryAsync(x=>x.JobId);
        jobs[requests[0].JobId].Status.ShouldBe(DownloadJobStatus.Stopped);
        foreach(var request in requests.Skip(1).Take(3)) { jobs[request.JobId].Status.ShouldBe(DownloadJobStatus.Failed); jobs[request.JobId].FailureKind.ShouldBe(FailureKind.Interrupted); }
        jobs[requests[4].JobId].Status.ShouldBe(DownloadJobStatus.Completed);
        (await f.Db.DownloadJobRuns.AsNoTracking().SingleAsync(x=>x.RunId==runs[0].RunId)).Status.ShouldBe(DownloadJobStatus.Stopped);
        (await f.Db.DownloadWorkerLeases.AsNoTracking().CountAsync(x=>x.Status==DownloadWorkerLeaseStatus.Active)).ShouldBe(0);
        (await f.Db.DownloadStageAttempts.AsNoTracking().CountAsync(x=>x.RunId!=runs[4].RunId && x.Status==DownloadStageStatus.Running)).ShouldBe(0);
        foreach(var execution in executions.Take(4))
        {
            (await repo.CanAcceptWorkerEventAsync(execution)).ShouldBeFalse();
            (await repo.CompleteStageAttemptAsync(execution)).ShouldBeFalse();
            (await repo.TryAcquireLeaseAsync(new() { Execution=execution,OccurredAt=Now,WorkerInstanceId="late-worker" })).Granted.ShouldBeFalse();
        }
        foreach(var request in requests.Take(4))
        {
            var group=(await f.Db.DownloadGroups.AsNoTracking().SingleAsync(g=>g.CorrelationId==request.CorrelationId));
            group.Status.ShouldBe(DownloadGroupStatus.Failed); (await repo.IsGroupExpansionAllowedAsync(request.CorrelationId)).ShouldBeFalse();
        }
        using var scope=f.ScopeFactory.CreateScope();
        var restarted=new DownloadFlowV2Repository(scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>(),new FixedClock(Now-Duration.FromDays(1)),NullDownloadJobStateNotifier.Instance);
        var again=await restarted.ReconcileForStartupAsync();
        again.StoppedQueuedJobs.ShouldBe(0); again.FailedActiveJobs.ShouldBe(0);
        again.GenerationStartedAt.ShouldBe(Now+Duration.FromTicks(20));
        var fresh=(await repo.StartFreshRunAsync(requests[1].JobId)).ShouldNotBeNull();
        fresh.RunId.ShouldNotBe(runs[1].RunId); fresh.RunNumber.ShouldBe(2);
        (await repo.CanAcceptGroupChildAsync(requests[1].CorrelationId)).ShouldBeFalse();
        (await repo.CanAcceptWorkerEventAsync(executions[1])).ShouldBeFalse();
        (await repo.BeginStageAttemptAsync(Execution(fresh),"fresh")).ShouldBeTrue();
    });

    [Test]
    public Task Real_Startup_Gate_Blocks_Watchdogs_Deletes_Download_State_And_Releases_Import_Recovery() => BothRuntime(async f=>
    {
        var store=f.Services.GetRequiredService<IFunctionStore>(); await store.Initialize();
        var repo=Repo(f.Db); var request=Request(); var run=(await repo.CreateInitialRunAsync(request,true)).ShouldNotBeNull();
        var serializer=new NodaTimeFlowSerializer();
        var type=await store.TypeStore.InsertOrGetStoredType(new(nameof(DownloadJobV2Flow)));
        var human=DownloadFlowInstance.Job(request.JobId,run.RunId); var id=new StoredId(type,StoredInstance.Create(human));
        var effect=StoredEffect.CreateCompleted(EffectId.CreateWithRootContext("progress"),[1]);
        await store.CreateFunction(id,new(human),serializer.Serialize(run),0,null,0,null,[effect]);
        await store.TimeoutStore.UpsertTimeout(new(id,EffectId.CreateWithRootContext("timeout"),0),false);
        var orphanHuman=DownloadFlowInstance.Job(Guid.NewGuid(),Guid.NewGuid()); var orphanId=new StoredId(type,StoredInstance.Create(orphanHuman));
        await store.CreateFunction(orphanId,new(orphanHuman),serializer.Serialize(run),0,0,0,null);
        var importType=await store.TypeStore.InsertOrGetStoredType(new("recoverable-import")); var importId=new StoredId(importType,StoredInstance.Create("item-attempt"));
        await store.CreateFunction(importId,new("item-attempt"),serializer.Serialize("value"),0,null,0,null);
        var terminalId=new StoredId(type,StoredInstance.Create("terminal")); await store.CreateFunction(terminalId,new("terminal"),serializer.Serialize(run),0,null,0,null);
        await store.SucceedFunction(terminalId,null,0,0,null,null,new(()=>null,0));
        var startup=f.Services.GetRequiredService<DownloadFlowStartupService>(); // resolving typed flows starts registry watchdogs
        var registry=f.Services.GetRequiredService<FlowsContainer>().Functions;
        var registration=registry.RegisterFunc<string,string>("recoverable-import",value=>Task.FromResult(value.ToUpperInvariant()));
        var state=f.Services.GetRequiredService<DownloadFlowStartupState>(); state.IsReady.ShouldBeFalse();
        (await store.GetExpiredFunctions(long.MaxValue)).ShouldBeEmpty(); (await store.TimeoutStore.GetTimeouts(long.MaxValue)).ShouldBeEmpty();
        await Task.Delay(100);
        (await store.GetFunction(id))!.Epoch.ShouldBe(0); (await store.GetFunction(importId))!.Status.ShouldBe(Status.Executing);
        await startup.StartAsync(default);
        state.IsReady.ShouldBeTrue(); state.GenerationStartedAt.ShouldBe(Now+Duration.FromTicks(10));
        (await store.GetFunction(id)).ShouldBeNull(); (await store.GetFunction(orphanId)).ShouldBeNull();
        (await store.TimeoutStore.GetTimeouts(id)).ShouldBeEmpty(); (await store.EffectsStore.GetEffectResults(id)).ShouldBeEmpty();
        (await store.GetFunction(terminalId))!.Status.ShouldBe(Status.Succeeded);
        var limit=DateTime.UtcNow+TimeSpan.FromSeconds(5);
        while((await store.GetFunction(importId))?.Status!=Status.Succeeded)
        { if(DateTime.UtcNow>limit) throw new TimeoutException("Import watchdog did not resume after readiness."); await Task.Delay(20); }
        (await registration.Invoke("item-attempt","ignored")).ShouldBe("VALUE");
        f.Services.GetRequiredService<IDurableJobPublisher>().ReceivedCalls().ShouldBeEmpty();
    });

    [Test]
    public Task Stale_Queued_Requests_And_Results_Are_Acknowledged_Without_Starting_Invalidated_Runs() => BothRuntime(async f=>
    {
        var repo=Repo(f.Db); var request=Request(); var oldRun=(await repo.CreateInitialRunAsync(request,true)).ShouldNotBeNull(); var execution=Execution(oldRun);
        await repo.BeginStageAttemptAsync(execution,"attempt"); await repo.TryAcquireLeaseAsync(new() { Execution=execution,OccurredAt=Now,WorkerInstanceId="worker" });
        await f.Services.GetRequiredService<DownloadFlowStartupService>().StartAsync(default);
        var state=f.Services.GetRequiredService<DownloadFlowStartupState>(); var consumer=f.Services.GetRequiredService<IDurableJobConsumer>();
        var jobs=f.Services.GetRequiredService<DownloadJobV2Flows>(); var groups=f.Services.GetRequiredService<DownloadGroupV2Flows>();
        var ingress=new DownloadRequestedIngressService(consumer,f.ScopeFactory,jobs,state,NullLogger<DownloadRequestedIngressService>.Instance);
        var queued=Request(); var context=Substitute.For<IDurableMessageContext<DownloadRequested>>(); context.Message.Returns(queued);
        await ingress.HandleAsync(context); await context.Received(1).AckAsync(); await context.DidNotReceive().NackAsync();
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync(x=>x.JobId==queued.JobId)).Status.ShouldBe(DownloadJobStatus.Stopped);
        (await f.Db.DownloadJobRuns.CountAsync(x=>x.JobId==queued.JobId)).ShouldBe(0);
        var redelivery=Substitute.For<IDurableMessageContext<DownloadRequested>>(); redelivery.Message.Returns(request);
        await ingress.HandleAsync(redelivery); await redelivery.Received(1).AckAsync();
        var result=new MetadataFetched { JobId=request.JobId,CorrelationId=request.CorrelationId,MessageId=Guid.NewGuid(),OperationKey="late",OccurredAt=Now,Execution=execution,Attempt=execution.Attempt };
        var resultContext=Substitute.For<IDurableMessageContext<MetadataFetched>>(); resultContext.Message.Returns(result);
        var events=new DownloadEventsConsumerService(consumer,f.ScopeFactory,jobs,NullLogger<DownloadEventsConsumerService>.Instance);
        await events.HandleAsync(resultContext); await resultContext.Received(1).AckAsync();
        var fresh=(await repo.StartFreshRunAsync(request.JobId)).ShouldNotBeNull();
        await events.HandleAsync(resultContext);
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync(x=>x.JobId==request.JobId)).CurrentRunId.ShouldBe(fresh.RunId);
        (await f.Db.DownloadJobHistory.CountAsync(x=>x.MessageId==result.MessageId)).ShouldBe(0);
        var staleGroup=new DownloadGroupRequested { GroupId=request.CorrelationId,CorrelationId=request.CorrelationId,MessageId=Guid.NewGuid(),OperationKey="old-group",OccurredAt=request.OccurredAt,Kind=DownloadGroupKind.Direct,SourceUrl=request.SourceUrl,DirectRequest=request };
        var groupContext=Substitute.For<IDurableMessageContext<DownloadGroupRequested>>(); groupContext.Message.Returns(staleGroup);
        var groupIngress=new DownloadGroupRequestedIngressService(consumer,f.ScopeFactory,groups,state,NullLogger<DownloadGroupRequestedIngressService>.Instance);
        await groupIngress.HandleAsync(groupContext); await groupContext.Received(1).AckAsync();
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync(x=>x.JobId==request.JobId)).Status.ShouldBe(DownloadJobStatus.Running);
        (await f.Db.DownloadGroups.AsNoTracking().SingleAsync(x=>x.CorrelationId==request.CorrelationId)).Status.ShouldBe(DownloadGroupStatus.Running);
        var child=Request();
        var staleNewGroup=staleGroup with { GroupId=child.CorrelationId,CorrelationId=child.CorrelationId,MessageId=Guid.NewGuid(),DirectRequest=child };
        var newGroupContext=Substitute.For<IDurableMessageContext<DownloadGroupRequested>>(); newGroupContext.Message.Returns(staleNewGroup);
        await groupIngress.HandleAsync(newGroupContext); await newGroupContext.Received(1).AckAsync();
        (await f.Db.DownloadGroups.AsNoTracking().SingleAsync(x=>x.GroupId==child.CorrelationId)).Status.ShouldBe(DownloadGroupStatus.Stopped);
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync(x=>x.JobId==child.JobId)).Status.ShouldBe(DownloadJobStatus.Stopped);
        (await f.Db.DownloadJobRuns.CountAsync(x=>x.JobId==child.JobId)).ShouldBe(0);
        f.Services.GetRequiredService<IDurableJobPublisher>().ReceivedCalls().ShouldBeEmpty();
    });

    [Test]
    public Task Cancelled_Startup_Leaves_Gate_Closed_And_Does_Not_Commit_Reconciliation() => BothRuntime(async f=>
    {
        var request=Request(); await Repo(f.Db).CreateInitialRunAsync(request,true);
        var startup=f.Services.GetRequiredService<DownloadFlowStartupService>();
        await Should.ThrowAsync<OperationCanceledException>(()=>startup.StartAsync(new CancellationToken(true)));
        f.Services.GetRequiredService<DownloadFlowStartupState>().IsReady.ShouldBeFalse();
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadJobStatus.Running);
        await startup.StartAsync(default); f.Services.GetRequiredService<DownloadFlowStartupState>().IsReady.ShouldBeTrue();
    });
    [Test]
    public Task Startup_Racing_A_Worker_Claim_Leaves_No_Active_Lease_Or_Resumable_Attempt() => Both(async f=>
    {
        var run=(await Repo(f.Db).CreateInitialRunAsync(Request(),true)).ShouldNotBeNull(); var execution=Execution(run);
        await Repo(f.Db).BeginStageAttemptAsync(execution,"attempt");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim=Task.Run(async()=>
        {
            using var scope=f.ScopeFactory.CreateScope(); await ready.Task;
            return await Repo(scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>()).TryAcquireLeaseAsync(new() { Execution=execution,OccurredAt=Now,WorkerInstanceId="racing-worker" });
        });
        var startup=Task.Run(async()=>
        {
            using var scope=f.ScopeFactory.CreateScope(); await ready.Task;
            return await Repo(scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>()).ReconcileForStartupAsync();
        });
        ready.SetResult(); await Task.WhenAll(claim,startup);
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadJobStatus.Failed);
        (await f.Db.DownloadStageAttempts.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadStageStatus.Failed);
        (await f.Db.DownloadWorkerLeases.CountAsync(x=>x.Status==DownloadWorkerLeaseStatus.Active)).ShouldBe(0);
        (await Repo(f.Db).CanAcceptWorkerEventAsync(execution)).ShouldBeFalse();
    });

    [Test]
    public Task Cancellation_While_Startup_Waits_For_Writer_Ownership_Does_Not_Publish_Readiness() => BothRuntime(async f=>
    {
        var request=Request(); await Repo(f.Db).CreateInitialRunAsync(request,true);
        var startup=f.Services.GetRequiredService<DownloadFlowStartupService>();
        using var scope=f.ScopeFactory.CreateScope(); var blocker=scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await using var transaction=await blocker.Database.BeginTransactionAsync();
        await blocker.DownloadJobs.Where(j=>j.JobId==request.JobId).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.WarningCount,10));
        using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(()=>Task.Run(()=>startup.StartAsync(cancelled.Token)));
        f.Services.GetRequiredService<DownloadFlowStartupState>().IsReady.ShouldBeFalse();
        await transaction.RollbackAsync();
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).Status.ShouldBe(DownloadJobStatus.Running);
        (await f.Db.DownloadJobs.AsNoTracking().SingleAsync()).WarningCount.ShouldBe(0);
        await startup.StartAsync(default); f.Services.GetRequiredService<DownloadFlowStartupState>().IsReady.ShouldBeTrue();
    });

}
