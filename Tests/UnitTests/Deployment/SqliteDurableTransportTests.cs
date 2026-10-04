using DataBridge.Messaging;
using DataBridge.Persistence;
using DataBridge.Persistence.Sqlite;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class SqliteDurableTransportTests
{
    private static SqliteDurableTransport Transport(string path, TimeSpan? lease = null) => new(
        new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, path, 5)),
        NullLogger<SqliteDurableTransport>.Instance, lease);

    [Test]
    public async Task Resumed_Workflow_Can_Publish_After_Its_Ingress_Delivery_Completes()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var bus = Transport(Path.Combine(directory, "test.db"));
            await bus.PublishAsync("test.input", 1, "ingress");
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loop = bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.input"), async context =>
            {
                // Cleipnir retains the ingress execution context while waiting for a worker result.
                var continuation = Task.Run(async () =>
                {
                    await resume.Task.WaitAsync(stop.Token);
                    await bus.PublishAsync("test.output", 42, "resumed-flow", cancellationToken: stop.Token);
                });
                await context.AckAsync(stop.Token);
                scheduled.TrySetResult(continuation);
            }, cancellationToken: stop.Token);
            var workflow = await scheduled.Task.WaitAsync(stop.Token);
            resume.TrySetResult();
            await workflow.WaitAsync(stop.Token);
            var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.output"), async context =>
            {
                await context.AckAsync(stop.Token);
                received.TrySetResult(context.Message);
            }, cancellationToken: stop.Token);
            (await received.Task.WaitAsync(stop.Token)).ShouldBe(42);
            stop.Cancel();
            try { await Task.WhenAll(loop, output); } catch (OperationCanceledException) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Consumer_Waits_For_Handler_Startup_Before_Delivering_Persisted_Work()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var path = Path.Combine(directory, "test.db");
            var gate = new Shared.Deployment.ApplicationStartupGate();
            var transport = new SqliteDurableTransport(new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, path, 5)),
                NullLogger<SqliteDurableTransport>.Instance, startup: gate);
            File.Exists(path).ShouldBeFalse();
            await transport.PublishAsync("test.input", 1, "before-startup");
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var consuming = transport.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.input"), async context =>
            {
                await context.AckAsync();
                delivered.TrySetResult();
            }, cancellationToken: stop.Token);
            await Task.Delay(100, stop.Token);
            delivered.Task.IsCompleted.ShouldBeFalse();
            gate.Release();
            await delivered.Task.WaitAsync(stop.Token);
            stop.Cancel();
            try { await consuming; } catch (OperationCanceledException) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Expired_Lease_Is_Reclaimed_And_Old_Acknowledgment_Is_Fenced()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "test.db");
            var first = Transport(path, TimeSpan.FromMilliseconds(100));
            await first.PublishAsync("test.input", 1, "input");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var claimed = new TaskCompletionSource<IDurableMessageContext<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var one = first.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.input"), async c =>
            {
                claimed.TrySetResult(c);
                await release.Task.WaitAsync(stop.Token);
            }, cancellationToken: stop.Token);
            var stale = await claimed.Task.WaitAsync(stop.Token);
            await Task.Delay(150, stop.Token);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var two = Transport(path).ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.input"), async c =>
            {
                c.Redelivered.ShouldBeTrue();
                await c.AckAsync(); done.TrySetResult();
            }, cancellationToken: stop.Token);
            await done.Task.WaitAsync(stop.Token);
            await Should.ThrowAsync<InvalidOperationException>(() => stale.AckAsync());
            release.TrySetResult(); stop.Cancel();
            try { await Task.WhenAll(one, two); } catch (OperationCanceledException) { }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Restart_Deduplicates_And_Commits_Outbox_With_Inbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.db");
        try
        {
            await Transport(path).PublishAsync("test.input", 42, "input");
            await Transport(path).PublishAsync("test.input", 42, "input");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bus = Transport(path);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var loop = bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.input"), async c =>
            {
                Interlocked.Increment(ref calls);
                c.Message.ShouldBe(42);
                await bus.PublishAsync("test.output", 7, "output");
                await c.AckAsync();
                received.TrySetResult();
            }, cancellationToken: stop.Token);
            await received.Task.WaitAsync(stop.Token);
            stop.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
            calls.ShouldBe(1);

            using var stop2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var restarted = Transport(path);
            var output = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputLoop = restarted.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.output"), async c =>
            {
                await c.AckAsync(); output.TrySetResult(c.Message);
            }, cancellationToken: stop2.Token);
            (await output.Task.WaitAsync(stop2.Token)).ShouldBe(7);
            stop2.Cancel();
            try { await outputLoop; } catch (OperationCanceledException) { }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Nack_Discards_Outbox_And_Retries_With_Delivery_Metadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var bus = Transport(Path.Combine(dir, "test.db"));
            await bus.PublishAsync("test.input", 1, "input");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loop = bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("test.input"), async c =>
            {
                if (!c.Redelivered)
                {
                    await bus.PublishAsync("test.output", 7, "discarded");
                    await c.NackAsync(TimeSpan.Zero);
                }
                else
                {
                    c.NumDelivered.ShouldBe(2u);
                    await c.AckAsync(); done.TrySetResult();
                }
            }, cancellationToken: stop.Token);
            await done.Task.WaitAsync(stop.Token);
            stop.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
            using var connection = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, Path.Combine(dir,"test.db"),5)).OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM messaging_messages WHERE subject='test.output'";
            ((long)command.ExecuteScalar()!).ShouldBe(0);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
