using DataBridge.Data;
using DataBridge.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;

namespace DataBridge.Persistence;

internal static class DownloadStartupGeneration
{
    public static async Task<Instant> AdvanceAsync(DataBridgeDbContext db, Instant now, CancellationToken ct)
    {
        // The non-relational provider is used only by existing isolated business-rule tests.
        if (!db.Database.IsRelational()) return now;
        var connection = db.Database.GetDbConnection();
        var postgres = connection is NpgsqlConnection;
        var sql = postgres ? """
            INSERT INTO jobs.download_startup_state(name,generation_started_at) VALUES('download',@now)
            ON CONFLICT(name) DO UPDATE SET generation_started_at=GREATEST(EXCLUDED.generation_started_at,
                download_startup_state.generation_started_at + INTERVAL '1 microsecond')
            RETURNING generation_started_at
            """ : """
            INSERT INTO jobs_download_startup_state(name,generation_started_at) VALUES('download',@now)
            ON CONFLICT(name) DO UPDATE SET generation_started_at=MAX(EXCLUDED.generation_started_at,
                jobs_download_startup_state.generation_started_at+1)
            RETURNING generation_started_at
            """;
        await using var command = ApplicationDbCommands.Create(connection,sql,db.Database.CurrentTransaction!.GetDbTransaction());
        // Use the next microsecond so timestamp truncation cannot admit a request published before readiness.
        // The persisted maximum also advances the fence when the wall clock stays equal or moves back.
        command.Parameters.AddWithValue("now",SqliteStorageEncoding.FromUnixMicroseconds(checked(SqliteStorageEncoding.ToUnixMicroseconds(now)+1)));
        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            Instant instant => instant,
            DateTime date => Instant.FromDateTimeUtc(date),
            long micros => SqliteStorageEncoding.FromUnixMicroseconds(micros),
            _ => throw new InvalidOperationException("Unexpected download generation timestamp mapping.")
        };
    }
}
