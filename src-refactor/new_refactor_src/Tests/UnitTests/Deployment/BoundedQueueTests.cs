using DataBridge.Messaging;
using DataBridge.Persistence;
using DataBridge.Persistence.Sqlite;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class BoundedQueueTests
{
    [Test]
    public async Task Idle_Probes_Back_Off_Without_Writer_Claims_And_Publication_Wakes_All_Consumers()
    {
        using var fixture = new Fixture();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = fixture.Bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("wanted.*"), async c =>
        {
            // Taking a different connection's writer lock proves the handler is outside the claim transaction.
            using var connection = fixture.Connections.OpenConnection();
            using (var transaction = connection.BeginTransaction()) { transaction.Commit(); }
            await c.AckAsync(); received.TrySetResult();
        }, cancellationToken: fixture.Stop.Token);
        var idleTimer = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(5500, fixture.Stop.Token);
        if (Environment.GetEnvironmentVariable("FROSTSTREAM_PHASE3_IDLE_REPORT") is { } reportPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(new { idleSeconds = idleTimer.Elapsed.TotalSeconds, readProbes = fixture.Bus.ReadProbes, writeClaims = fixture.Bus.WriteClaims, consumers = 1, maxIdleSeconds = 5 }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        fixture.Bus.WriteClaims.ShouldBe(0);
        fixture.Bus.ReadProbes.ShouldBeLessThanOrEqualTo(12);
        for (var i = 0; i < 200; i++) await fixture.Bus.PublishAsync("unrelated.input", i, "other-" + i);
        await fixture.Bus.PublishAsync("wanted.input", 1, "wanted");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Bus.WriteClaims.ShouldBe(1);
        using (var connection = fixture.Connections.OpenConnection())
        {
            using var plan = connection.CreateCommand();
            plan.CommandText = "EXPLAIN QUERY PLAN SELECT seq FROM messaging_inbox WHERE consumer='test/wanted.*' AND acknowledged=0 AND available<=0 ORDER BY available,seq LIMIT 1";
            using var reader = plan.ExecuteReader(); var details = new List<string>(); while (reader.Read()) details.Add(reader.GetString(3));
            string.Join(" ", details).ShouldContain("messaging_inbox_claim");
        }
        fixture.Stop.Cancel(); await IgnoreCancellation(loop);
    }
    [Test]
    public async Task Poison_Delivery_Is_Inspectable_Retryable_And_Retention_Preserves_Dedup_And_Active_Fanout()
    {
        using var fixture = new Fixture(new() { MaxAttempts = 2, CompletedRetention = TimeSpan.Zero });
        var healthy = false;
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = fixture.Bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("input"), async c => { await c.AckAsync(); observed.TrySetResult(); }, new() { DurableName = ConsumerName.From("observer") }, fixture.Stop.Token);
        await fixture.Bus.PublishAsync("input", 1, "once");
        var loop = fixture.Bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("input"), async c =>
        {
            if (healthy) { await c.AckAsync(); acked.TrySetResult(); } else await c.NackAsync(TimeSpan.Zero);
        }, cancellationToken: fixture.Stop.Token);
        IReadOnlyList<LocalQueueFailure> failures = [];
        for (var i = 0; i < 100 && failures.Count == 0; i++) { failures = await fixture.Bus.GetFailuresAsync(); await Task.Delay(20); }
        failures.Count.ShouldBe(1);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        (await fixture.Bus.MaintainAsync()).ShouldBe(0);
        healthy = true;
        await fixture.Bus.RetryAsync(failures[0].Consumer, failures[0].Sequence);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        (await fixture.Bus.GetFailuresAsync()).ShouldBeEmpty();
        (await fixture.Bus.MaintainAsync()).ShouldBe(1);
        await fixture.Bus.PublishAsync("input", 1, "once");
        using var connection = fixture.Connections.OpenConnection();
        using var count = connection.CreateCommand(); count.CommandText = "SELECT count(*) FROM messaging_messages";
        Convert.ToInt64(count.ExecuteScalar()).ShouldBe(0);
        fixture.Stop.Cancel(); await IgnoreCancellation(Task.WhenAll(loop, observer));
    }
    [Test]
    public async Task Fanout_Retention_Waits_For_Every_Delivery_And_Concurrency_Is_Capped()
    {
        using var fixture = new Fixture(new() { MaxConcurrentHandlers = 2, MaxConsumerConcurrency = 2, CompletedRetention = TimeSpan.Zero });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var peak = 0;
        var loop = fixture.Bus.ConsumeAsync<int>(StreamName.From("test"), SubjectName.From("input"), async c =>
        {
            var count = Interlocked.Increment(ref active); peak = Math.Max(peak, count); if (count == 2) entered.TrySetResult();
            await release.Task.WaitAsync(fixture.Stop.Token); await c.AckAsync(); Interlocked.Decrement(ref active);
        }, new() { MaxConcurrency = 100, BatchSize = 100 }, fixture.Stop.Token);
        for (var i = 0; i < 5; i++) await fixture.Bus.PublishAsync("input", i, "job-" + i);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        (await fixture.Bus.MaintainAsync()).ShouldBe(0); peak.ShouldBe(2);
        release.TrySetResult();
        for (var i = 0; i < 100 && fixture.Bus.WriteClaims < 5; i++) await Task.Delay(20);
        fixture.Stop.Cancel(); await IgnoreCancellation(loop);
        peak.ShouldBe(2);
    }
    private static async Task IgnoreCancellation(Task task) { try { await task; } catch (OperationCanceledException) { } }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "phase3-queue-" + Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Connections { get; }
        public SqliteDurableTransport Bus { get; }
        public CancellationTokenSource Stop { get; } = new(TimeSpan.FromSeconds(15));
        public Fixture(LocalQueueOptions? options = null)
        {
            Connections = new(new(PersistenceProvider.Sqlite, Path.Combine(directory, "test.db"), 5));
            Bus = new(Connections, NullLogger<SqliteDurableTransport>.Instance, settings: options);
        }
        public void Dispose() { Stop.Cancel(); Stop.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
