using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using DataBridge.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;


namespace DataBridge.Persistence;

/// <summary>Owns complete database-only mutations. Nested repository calls join the same transaction.</summary>
public static class ApplicationMutation
{
    private sealed class MutationState
    {
        public List<Func<Task>> AfterCommit { get; } = [];
    }

    private static readonly ConditionalWeakTable<DataBridgeDbContext, MutationState> Active = new();
    private static readonly Meter Meter = new("FrostStream.Persistence");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("persistence.mutation.retries");
    private static readonly Counter<long> Cancellations = Meter.CreateCounter<long>("persistence.mutation.cancellations");
    private static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("persistence.mutation.timeouts");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("persistence.mutation.duration", "ms");

    public static Task MutateAsync(this DataBridgeDbContext db, string operation, Func<Task> action, CancellationToken ct = default)
        => db.MutateAsync(operation, async () => { await action(); return true; }, ct);

    public static async Task<T> MutateAsync<T>(this DataBridgeDbContext db, string operation, Func<Task<T>> action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Active.TryGetValue(db, out _)) return await action();
        // An externally owned transaction cannot safely be replayed or committed here.
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Application mutations must own the transaction; nest through MutateAsync instead.");

        var relational = db.Database.IsRelational();
        var dialect = relational ? PersistenceDialects.For(db.Database.GetDbConnection()) : null;
        var sqlite = dialect?.Provider == PersistenceProvider.Sqlite ? db.Database.GetDbConnection() : null;
        var budget = TimeSpan.FromSeconds(relational ? dialect!.MutationTimeout(db.Database.GetDbConnection()) : 5);
        if (relational && db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Save pending changes before starting an application mutation.");
        var started = Stopwatch.StartNew();
        var tags = new TagList { { "operation", operation }, { "provider", !relational ? "inmemory" : sqlite is null ? "postgres" : "sqlite" } };
        T result;
        List<Func<Task>> committedCallbacks;
        var committed = false;
        var openedHere = sqlite is not null && sqlite.State != ConnectionState.Open;
        try
        {
            // EF's connection interceptor resets DefaultTimeout when opening. Open first so the
            // one-second transaction acquisition limit below is applied after connection setup.
            if (openedHere) await db.Database.OpenConnectionAsync(ct);
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var state = new MutationState();
                Active.Add(db, state);
                try
                {
                    // Never reuse a tracked snapshot from before writer ownership. Pending caller changes
                    // belong to the first attempt and are rejected rather than silently lost on replay.
                    if (relational) db.ChangeTracker.Clear();
                    await using var transaction = relational ? await dialect!.BeginAsync(db, ct) : null;
                    result = await action();
                    if (transaction is not null) await transaction.CommitAsync(ct);
                    committed = true;
                    committedCallbacks = state.AfterCommit;
                    break;
                }
                catch (Exception exception) when (IsContention(exception) || IsUpsertRace(exception, operation))
                {
                    db.ChangeTracker.Clear();
                    ct.ThrowIfCancellationRequested();
                    if (started.Elapsed >= budget || attempt >= 7)
                    {
                        Timeouts.Add(1, tags);
                        Duration.Record(started.Elapsed.TotalMilliseconds, tags);
                        throw;
                    }
                    Retries.Add(1, tags);
                    var remaining = budget - started.Elapsed;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(remaining.TotalMilliseconds, 20 * (attempt + 1) + Random.Shared.Next(20)))), ct);
                }
                catch
                {
                    if (relational) db.ChangeTracker.Clear();
                    throw;
                }
                finally { Active.Remove(db); }
            }
            Duration.Record(started.Elapsed.TotalMilliseconds, tags);
            // This is deliberately outside the retry loop and after disposing the transaction.
            foreach (var callback in committedCallbacks) await callback();
            return result;
        }
        catch (OperationCanceledException) when (!committed)
        {
            Cancellations.Add(1, tags);
            Duration.Record(started.Elapsed.TotalMilliseconds, tags);
            throw;
        }
        finally
        {
            if (openedHere) await db.Database.CloseConnectionAsync();
        }
    }

    public static Task AfterCommitAsync(this DataBridgeDbContext db, Func<Task> callback)
    {
        if (!Active.TryGetValue(db, out var state)) return callback();
        state.AfterCommit.Add(callback);
        return Task.CompletedTask;
    }

    public static bool IsContention(Exception exception)
    {
        if (exception is (DbUpdateException or InvalidOperationException) && exception.InnerException is { } inner) return IsContention(inner);
        return PersistenceDialects.IsContention(exception);
    }

    private static bool IsUpsertRace(Exception exception, string operation)
    {
        if (exception is (DbUpdateException or InvalidOperationException) && exception.InnerException is { } inner)
            return IsUpsertRace(inner, operation);
        var constraint = PersistenceDialects.UniqueConstraint(exception);
        if (constraint is null) return false;
        // Serializable PostgreSQL may report a competing insert as unique_violation instead of
        // serialization_failure. Replay only natural keys these methods already read and reuse.
        // Other unique, check, FK and validation errors remain visible without a retry.
        return (operation, constraint) switch
        {
            ("DownloadJobsRepository.ReserveVersionAsync", "ux_media_content_id_versions_storage_key_content_hash" or "ux_media_content_id_versions_media_guid_version_num") => true,
            ("CreatorDiscoveryRepository.UpsertDiscoveredMediaBatchAsync", "ux_discovered_media_identity" or "PK_creator_scan_state") => true,
            ("CreatorDiscoveryRepository.CreateOrReuseSourceAsync", "uq_creator_sources_source_url") => true,
            ("PlaylistsRepository.CreateOrReuseAsync", "ux_playlists_source_url") => true,
            ("PlaylistsRepository.FanOutEntryAsync", "ux_playlist_items_playlist_id_entry_url" or "PK_download_jobs") => true,
            ("DownloadFlowV2Repository.CreateInitialRunAsync" or "DownloadFlowV2Repository.AcceptGroupRequestAsync", "PK_download_jobs") => true,
            ("DownloadFlowV2Repository.CreateGroupIfMissingAsync" or "DownloadFlowV2Repository.AcceptGroupRequestAsync", "PK_download_groups" or "ux_download_groups_correlation_id") => true,
            ("DownloadFlowV2Repository.UpsertArtifactAsync", "ux_download_artifacts_run_key") => true,
            _ => false
        };
    }

}
