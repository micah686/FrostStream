using DataBridge;
using DataBridge.Persistence;
using DataBridge.Persistence.Indexing;
using DataBridge.Search;
using FrostStream.Lite.Infrastructure;
using FrostStream.Lite.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Search;

public sealed class SearchActivationBarrierTests
{
    [Test]
    public async Task Activation_Blocks_Source_Mutations_Allows_Wal_Reads_And_Preserves_New_Revisions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lucene-barrier-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
            builder.Configuration["Deployment:Mode"] = "Lite";
            builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
            builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "test.db");
            builder.AddLitePersistence(); builder.AddLuceneSearch();
            using var host = builder.Build(); host.InitializeDataBridge();
            var database = host.Services.GetRequiredService<ApplicationDatabase>();
            var store = host.Services.GetRequiredService<PendingIndexStore>();
            var barrier = host.Services.GetRequiredService<ISearchActivationBarrier>();
            var id = Guid.NewGuid(); await store.RequestAsync(id);
            var before = (await store.GetRebuildChangesAsync("", 10, default)).Single();
            await store.RecordRebuildReplayAsync(before, default);
            Task? writing = null;
            await barrier.RunAsync(async ct =>
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                writing = Task.Run(async () =>
                {
                    started.SetResult();
                    await using var command = database.CreateCommand($"INSERT INTO {database.Table("media.media")}(media_guid) VALUES(@id)");
                    command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync(ct);
                });
                await started.Task;
                await Task.Delay(100, ct);
                writing.IsCompleted.ShouldBeFalse("SQLite writes must wait until activation completes");
                // Separate WAL readers continue while BEGIN IMMEDIATE excludes mutations.
                (await store.GetRebuildChangesAsync("", 10, ct)).ShouldBeEmpty();
            }, default);
            await writing!.WaitAsync(TimeSpan.FromSeconds(5));
            var changed = (await store.GetRebuildChangesAsync("", 10, default)).Single();
            changed.Revision.ShouldBeGreaterThan(before.Revision);
            // Catch-up checkpoints cannot acknowledge the normal durable projection queue.
            (await store.ClaimAsync(default)).ShouldNotBeNull().MediaGuid.ShouldBe(id.ToString("N"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
