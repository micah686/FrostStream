using DataBridge.Lite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using Shared.Application;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Lite;

/// <summary>
/// Real-driver ownership checks. The phase verification command supplies a disposable PostgreSQL
/// connection through FROSTSTREAM_TEST_POSTGRES; ordinary unit runs leave external infrastructure alone.
/// </summary>
public sealed class LiteExecutionPostgresTests
{
    [Test]
    [NotInParallel("LiteExecutionPostgres")]
    public async Task Advisory_Ownership_Rejects_A_Second_Runtime_And_Detects_Backend_Loss()
    {
        var connectionString = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var firstLifetime = new TestApplicationLifetime();
        await using var first = Lease(dataSource, firstLifetime);
        await first.StartAsync(CancellationToken.None);
        first.IsOwned.ShouldBeTrue();
        first.BackendProcessId.ShouldNotBeNull();

        await using var rejected = Lease(dataSource, new TestApplicationLifetime());
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => rejected.StartAsync(CancellationToken.None));
        error.Message.ShouldContain("already owns execution");

        await using (var killer = dataSource.CreateCommand("SELECT pg_terminate_backend($1)"))
        {
            killer.Parameters.AddWithValue(first.BackendProcessId.Value);
            (await killer.ExecuteScalarAsync()).ShouldBe(true);
        }

        await WaitUntilAsync(() => first.OwnershipLost.IsCancellationRequested, TimeSpan.FromSeconds(5));
        firstLifetime.Stopping.IsCancellationRequested.ShouldBeTrue();
        first.IsOwned.ShouldBeFalse();
        await Should.ThrowAsync<Exception>(() => first.ExecuteOwnedAsync(
            (_, _) => Task.FromResult(true), CancellationToken.None));

        await using var replacement = Lease(dataSource, new TestApplicationLifetime());
        await replacement.StartAsync(CancellationToken.None);
        replacement.IsOwned.ShouldBeTrue();
        await replacement.StopAsync(CancellationToken.None);
    }

    [Test]
    [NotInParallel("LiteExecutionPostgres")]
    public async Task Ledger_Deduplicates_Recovers_And_Uses_Idempotent_Completion()
    {
        var connectionString = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await RecreateLedgerAsync(dataSource);
        var crashedId = Guid.Empty;

        await using (var first = Lease(dataSource, new TestApplicationLifetime()))
        {
            await first.StartAsync(CancellationToken.None);
            var store = new NpgsqlLocalExecutionStore(first, dataSource);
            var original = await store.EnqueueAsync("fixture", "duplicate-key", JsonDocument.Parse("{\"value\":1}").RootElement);
            var duplicate = await store.EnqueueAsync("fixture", "duplicate-key", JsonDocument.Parse("{\"value\":2}").RootElement);
            duplicate.WorkId.ShouldBe(original.WorkId);

            var claimed = await store.ClaimNextAsync();
            claimed.ShouldNotBeNull();
            (await store.ReportProgressAsync(claimed.WorkId, 1, 42.5, "controlled progress")).ShouldBeTrue();
            (await store.ReportProgressAsync(claimed.WorkId, 1, 99, "stale progress")).ShouldBeFalse();
            await store.CompleteAsync(claimed, new WorkExecutionResult(WorkExecutionDisposition.Completed));
            (await store.CompleteAsync(claimed, new WorkExecutionResult(WorkExecutionDisposition.Completed))).ShouldBeNull();

            await store.EnqueueAsync("fixture", "retry", JsonDocument.Parse("{}").RootElement);
            var retrying = (await store.ClaimNextAsync()).ShouldNotBeNull();
            (await store.CompleteAsync(retrying, new WorkExecutionResult(
                WorkExecutionDisposition.Retry, "controlled_retry", RetryAfter: TimeSpan.Zero)))
                .ShouldBe(LocalExecutionStatus.RetryWaiting);
            var retried = (await store.ClaimNextAsync()).ShouldNotBeNull();
            retried.WorkId.ShouldBe(retrying.WorkId);
            retried.Attempt.ShouldBe(2);
            (await store.CompleteAsync(retried, new WorkExecutionResult(WorkExecutionDisposition.Completed)))
                .ShouldBe(LocalExecutionStatus.Completed);

            var interrupted = await store.EnqueueAsync("fixture", "interrupted", JsonDocument.Parse("{}").RootElement);
            crashedId = (await store.ClaimNextAsync()).ShouldNotBeNull().WorkId;
            crashedId.ShouldBe(interrupted.WorkId);
            // Disposing the owning session models an ungraceful process loss: the row remains running.
        }

        await using var replacement = Lease(dataSource, new TestApplicationLifetime());
        await replacement.StartAsync(CancellationToken.None);
        var recoveredStore = new NpgsqlLocalExecutionStore(replacement, dataSource);
        await recoveredStore.RecoverInterruptedAsync();
        var recovered = await recoveredStore.ClaimNextAsync();
        recovered.ShouldNotBeNull();
        recovered.WorkId.ShouldBe(crashedId);
        recovered.Attempt.ShouldBe(2);
        await recoveredStore.CompleteAsync(recovered, new WorkExecutionResult(WorkExecutionDisposition.Completed));

        var snapshot = await recoveredStore.LoadSnapshotAsync(crashedId.ToString());
        snapshot.Items.Count.ShouldBe(1);
        snapshot.Items[0].Status.ShouldBe(LocalExecutionStatus.Completed);
        await replacement.StopAsync(CancellationToken.None);
    }

    private static LiteExecutionLease Lease(NpgsqlDataSource dataSource, TestApplicationLifetime lifetime)
        => new(dataSource, Options.Create(new global::DataBridge.Lite.LiteExecutionOptions
        {
            OwnershipProbeInterval = TimeSpan.FromMilliseconds(25)
        }), lifetime, NullLogger<LiteExecutionLease>.Instance);

    private static async Task RecreateLedgerAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE SCHEMA IF NOT EXISTS jobs;
            DROP TABLE IF EXISTS jobs.local_execution_work;
            CREATE TABLE jobs.local_execution_work (
                work_id uuid PRIMARY KEY,
                kind varchar(128) NOT NULL,
                deduplication_key varchar(512) NOT NULL UNIQUE,
                payload jsonb NOT NULL,
                status varchar(32) NOT NULL,
                attempt integer NOT NULL DEFAULT 0,
                maximum_attempts integer NOT NULL DEFAULT 3,
                available_at timestamptz NOT NULL DEFAULT now(),
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                completed_at timestamptz NULL,
                error_code varchar(128) NULL,
                error_message varchar(4096) NULL,
                progress_sequence integer NOT NULL DEFAULT 0,
                progress_percent double precision NULL,
                progress_message varchar(2048) NULL
            );
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Condition was not observed before the deadline.");
            await Task.Delay(20);
        }
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public CancellationToken Stopping => _stopping.Token;
        public void StopApplication() => _stopping.Cancel();
    }
}
