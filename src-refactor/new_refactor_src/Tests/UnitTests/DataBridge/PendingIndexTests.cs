using NSubstitute;
using DataBridge;
using DataBridge.Persistence;
using DataBridge.Persistence.Indexing;
using FrostStream.Full.Infrastructure;
using FrostStream.Lite.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace UnitTests.DataBridge;

public sealed class PendingIndexTests
{
    [Test]
    public async Task Mutation_And_Intent_Roll_Back_Together_And_Committed_Intent_Survives_A_New_Host()
    {
        foreach (var postgres in Providers())
        {
            using var fixture = new Fixture(postgres);
            var id = Guid.NewGuid();
            await using (var connection = await fixture.Database.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = ApplicationDbCommands.Create(connection, $"INSERT INTO {fixture.Database.Table("media.media")}(media_guid) VALUES(@id)", transaction);
                command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
            }
            (await fixture.Store.ClaimAsync(default)).ShouldBeNull();
            await fixture.Insert(id);
            var restarted = new PendingIndexStore(fixture.Database);
            var first = (await restarted.ClaimAsync(default))!; first.ShouldNotBeNull();
            await restarted.RequestAsync(id); // Concurrent mutation while an older revision is indexing.
            await restarted.CompleteAsync(first, default);
            var next = (await restarted.ClaimAsync(default))!; next.Revision.ShouldBeGreaterThan(first.Revision);
            await restarted.CompleteAsync(next, default);
            (await restarted.ClaimAsync(default)).ShouldBeNull();
            await using var delete = fixture.Database.CreateCommand($"DELETE FROM {fixture.Database.Table("media.media")} WHERE media_guid=@id");
            delete.Parameters.AddWithValue("@id", id); await delete.ExecuteNonQueryAsync();
            var tombstone = (await restarted.ClaimAsync(default))!; tombstone.MediaGuid.ShouldBe(id.ToString("N"));
            await restarted.FailAsync(tombstone, new IOException("provider unavailable"), default);
            (await restarted.GetFailuresAsync()).Single().Error.ShouldBe("provider unavailable");
            await restarted.RequestAsync(id);
            var retry = (await restarted.ClaimAsync(default))!; retry.ShouldNotBeNull();
            await restarted.CompleteAsync(retry, default);
        }
    }
    [Test]
    public async Task Expired_Index_Lease_Is_Reclaimed_And_Stale_Completion_Is_Fenced()
    {
        foreach (var postgres in Providers())
        {
            using var fixture = new Fixture(postgres); var id = Guid.NewGuid(); await fixture.Insert(id);
            var stale = (await fixture.Store.ClaimAsync(default))!;
            (await new PendingIndexStore(fixture.Database).ClaimAsync(default)).ShouldBeNull();
            await using (var command = fixture.Database.CreateCommand("UPDATE search_pending_index SET lease_until=0")) await command.ExecuteNonQueryAsync();
            var replacement = (await fixture.Store.ClaimAsync(default))!;
            await Should.ThrowAsync<InvalidOperationException>(() => fixture.Store.CompleteAsync(stale, default));
            await fixture.Store.CompleteAsync(replacement, default);
        }
    }
    [Test]
    public async Task Projection_Dependencies_Invalidate_On_Changes_And_Rebuild_Replays_Applied_Revisions()
    {
        foreach (var postgres in Providers())
        {
            using var fixture = new Fixture(postgres); var id = Guid.NewGuid(); await fixture.Insert(id);
            await Drain(fixture.Store);
            var timestamp = postgres ? "CURRENT_TIMESTAMP" : "0";
            await using (var watch = fixture.Database.CreateCommand($"INSERT INTO {fixture.Database.Table("media.watch_states")}(owner_subject,media_guid,last_played_at) VALUES('test',@id,{timestamp})"))
            {
                watch.Parameters.AddWithValue("@id", id); await watch.ExecuteNonQueryAsync();
            }
            (await fixture.Store.ClaimAsync(default)).ShouldBeNull();
            await using var insert = fixture.Database.CreateCommand($"INSERT INTO {fixture.Database.Table("metadata.media_base")}(media_guid) VALUES(@id) RETURNING id");
            insert.Parameters.AddWithValue("@id", id); var baseId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            await Drain(fixture.Store);
            await using var stream = fixture.Database.CreateCommand($"INSERT INTO {fixture.Database.Table("metadata.media_streams")}(media_base_id,stream_type,codec_name,codec_long_name) VALUES(@base,'video','h264','H264') RETURNING id");
            stream.Parameters.AddWithValue("@base", baseId); var streamId = Convert.ToInt64(await stream.ExecuteScalarAsync());
            var change = (await fixture.Store.ClaimAsync(default))!; change.MediaGuid.ShouldBe(id.ToString("N")); await fixture.Store.CompleteAsync(change, default);
            await using var delete = fixture.Database.CreateCommand($"DELETE FROM {fixture.Database.Table("metadata.media_streams")} WHERE id=@id");
            delete.Parameters.AddWithValue("@id", streamId); await delete.ExecuteNonQueryAsync();
            var removed = (await fixture.Store.ClaimAsync(default))!; removed.Revision.ShouldBeGreaterThan(change.Revision); await fixture.Store.CompleteAsync(removed, default);
            await fixture.Store.InvalidateAsync(default);
            var rebuild = (await fixture.Store.ClaimAsync(default))!; rebuild.Revision.ShouldBeGreaterThan(removed.Revision);
        }
    }
    [Test]
    public async Task Provider_Failure_Leaves_Deletion_Intent_For_A_New_Worker_And_All_Collections_Must_Complete()
    {
        using var fixture = new Fixture(false); var id = Guid.NewGuid(); await fixture.Store.RequestAsync(id);
        var query = NSubstitute.Substitute.For<global::DataBridge.Search.IMediaDocumentQuery>();
        var index = NSubstitute.Substitute.For<global::DataBridge.Search.ITypesenseIndexService>();
        var fail = true;
        index.DeleteCommentsByMediaGuidAsync(NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<CancellationToken>()).Returns(_ => fail ? Task.FromException(new IOException("offline")) : Task.CompletedTask);
        var hydrator = new global::DataBridge.Search.CaptionDocumentHydrator(NSubstitute.Substitute.For<Shared.Storage.IStoreProvider>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<global::DataBridge.Search.CaptionDocumentHydrator>.Instance);
        var gate = new global::DataBridge.Search.TypesenseWriterGate();
        global::DataBridge.Search.PendingIndexWorker Worker() => new(fixture.Store, query, index, hydrator, gate, Microsoft.Extensions.Logging.Abstractions.NullLogger<global::DataBridge.Search.PendingIndexWorker>.Instance);
        using (var first = Worker())
        {
            await first.StartAsync(default);
            for (var i = 0; i < 100 && (await fixture.Store.GetFailuresAsync()).Count == 0; i++) await Task.Delay(20);
            (await fixture.Store.GetFailuresAsync()).Single().Error.ShouldBe("offline");
            await first.StopAsync(default);
        }
        fail = false; await fixture.Store.RequestAsync(id);
        using (var second = Worker())
        {
            await second.StartAsync(default);
            for (var i = 0; i < 100; i++)
            {
                await using var count = fixture.Database.CreateCommand("SELECT count(*) FROM search_pending_index WHERE revision>applied");
                if (Convert.ToInt64(await count.ExecuteScalarAsync()) == 0) break;
                await Task.Delay(20);
            }
            await second.StopAsync(default);
        }
        (await fixture.Store.ClaimAsync(default)).ShouldBeNull();
        await index.Received().DeleteMediaByGuidAsync(id.ToString("N"), NSubstitute.Arg.Any<CancellationToken>());
        await index.Received().DeleteCaptionsByMediaGuidAsync(id.ToString("N"), NSubstitute.Arg.Any<CancellationToken>());
    }
    [Test]
    public async Task Index_Worker_Waits_For_Application_Handlers_Before_Resolving_Stored_Content()
    {
        using var fixture=new Fixture(false); var id=Guid.NewGuid(); await fixture.Store.RequestAsync(id);
        var startup=new Shared.Deployment.ApplicationStartupGate();
        var applied=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query=Substitute.For<global::DataBridge.Search.IMediaDocumentQuery>();
        var index=Substitute.For<global::DataBridge.Search.ITypesenseIndexService>();
        index.DeleteCaptionsByMediaGuidAsync(Arg.Any<string>(),Arg.Any<CancellationToken>()).Returns(_=>{applied.TrySetResult();return Task.CompletedTask;});
        var hydrator=new global::DataBridge.Search.CaptionDocumentHydrator(Substitute.For<Shared.Storage.IStoreProvider>(),Microsoft.Extensions.Logging.Abstractions.NullLogger<global::DataBridge.Search.CaptionDocumentHydrator>.Instance);
        using var worker=new global::DataBridge.Search.PendingIndexWorker(fixture.Store,query,index,hydrator,new(),Microsoft.Extensions.Logging.Abstractions.NullLogger<global::DataBridge.Search.PendingIndexWorker>.Instance,startup);
        await worker.StartAsync(default); await Task.Delay(100);
        applied.Task.IsCompleted.ShouldBeFalse();
        startup.Release(); await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await worker.StopAsync(default);
    }
    [Test]
    public async Task Committed_Index_Intent_Survives_Killing_The_Writer_Process()
    {
        foreach (var postgres in Providers())
        {
            using var fixture = new Fixture(postgres); var id = Guid.NewGuid();
            var marker = Path.Combine(Path.GetTempPath(), "phase3-commit-" + Guid.NewGuid().ToString("N"));
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(PendingIndexTests).Assembly.Location);
            start.ArgumentList.Add("--treenode-filter"); start.ArgumentList.Add("/*/*/PendingIndexTests/CommitProcessProbe");
            start.Environment["FROSTSTREAM_PHASE3_PROBE_PROVIDER"] = postgres ? "Postgres" : "Sqlite";
            start.Environment["FROSTSTREAM_PHASE3_PROBE_CONNECTION"] = postgres ? fixture.Host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:froststreamdb"]! : fixture.Host.Services.GetRequiredService<PersistenceOptions>().SqlitePath;
            start.Environment["FROSTSTREAM_PHASE3_PROBE_ID"] = id.ToString("N"); start.Environment["FROSTSTREAM_PHASE3_PROBE_MARKER"] = marker;
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(marker))
                {
                    if (process.HasExited) throw new InvalidOperationException(await stdout + await stderr);
                    await Task.Delay(20, timeout.Token);
                }
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
                var work = (await new PendingIndexStore(fixture.Database).ClaimAsync(default))!;
                work.MediaGuid.ShouldBe(id.ToString("N")); await fixture.Store.CompleteAsync(work, default);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr); File.Delete(marker);
            }
        }
    }
    [Test]
    public async Task CommitProcessProbe()
    {
        var settings = Environment.GetEnvironmentVariable("FROSTSTREAM_PHASE3_PROBE_CONNECTION"); if (settings is null) return;
        var postgres = Environment.GetEnvironmentVariable("FROSTSTREAM_PHASE3_PROBE_PROVIDER") == "Postgres";
        await using System.Data.Common.DbConnection connection = postgres ? new NpgsqlConnection(settings) : new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = settings }.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO " + (postgres ? "media.media" : "media_media") + "(media_guid) VALUES(@id)";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@id";
            var id = Guid.Parse(Environment.GetEnvironmentVariable("FROSTSTREAM_PHASE3_PROBE_ID")!);
            parameter.Value = postgres ? id : id.ToString("N"); command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        }
        File.WriteAllText(Environment.GetEnvironmentVariable("FROSTSTREAM_PHASE3_PROBE_MARKER")!, "committed");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    private static async Task Drain(PendingIndexStore store)
    {
        while (await store.ClaimAsync(default) is { } work) await store.CompleteAsync(work, default);
    }
    private static IEnumerable<bool> Providers() => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES")) ? [false] : [false, true];
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "phase3-index-" + Guid.NewGuid().ToString("N"));
        private readonly string? admin; private readonly string databaseName = "phase3_" + Guid.NewGuid().ToString("N");
        public IHost Host { get; }
        public ApplicationDatabase Database => Host.Services.GetRequiredService<ApplicationDatabase>();
        public PendingIndexStore Store => Host.Services.GetRequiredService<PendingIndexStore>();
        public Fixture(bool postgres)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
            if (postgres)
            {
                admin = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES")!;
                using var connection = new NpgsqlConnection(admin); connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE {databaseName}"; command.ExecuteNonQuery();
                builder.Configuration["ConnectionStrings:froststreamdb"] = new NpgsqlConnectionStringBuilder(admin) { Database = databaseName }.ConnectionString;
                builder.AddFullPersistence();
            }
            else
            {
                builder.Configuration["Deployment:Mode"] = "Lite"; builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
                builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "test.db"); builder.AddLitePersistence();
            }
            Host = builder.Build(); Host.InitializeDataBridge();
        }
        public async Task Insert(Guid id)
        {
            await using var command = Database.CreateCommand($"INSERT INTO {Database.Table("media.media")}(media_guid) VALUES(@id)");
            command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync();
        }
        public void Dispose()
        {
            Host.Dispose();
            if (admin is not null) { using var connection = new NpgsqlConnection(admin); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE {databaseName} WITH (FORCE)"; command.ExecuteNonQuery(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
