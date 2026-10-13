using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Quartz;

namespace DataBridge.Persistence.Postgres;

/// <summary>Checksummed fresh-install catalog, applied atomically under database ownership.</summary>
internal sealed class PostgresSchemaInitializer(NpgsqlDataSource source) : IApplicationSchemaInitializer
{
    private static readonly string Sql = ReadBaseline();
    internal static readonly string Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
    private static string ReadBaseline()
    {
        using var stream = typeof(PostgresSchemaInitializer).Assembly.GetManifestResourceStream("FrostStream.Postgres.Baseline.sql")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public void Initialize(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = source.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SET LOCAL lock_timeout='10s'; SELECT pg_advisory_xact_lock(70606001)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT to_regclass('public.froststream_installation_baseline') IS NOT NULL";
        if ((bool)command.ExecuteScalar()!)
        {
            command.CommandText = "SELECT version,checksum FROM public.froststream_installation_baseline";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != 1 || reader.GetString(1) != Checksum || reader.Read())
                throw new InvalidOperationException("PostgreSQL has an unknown or changed fresh-install baseline. Use a compatible application version.");
        }
        else
        {
            command.CommandText = "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema','cleipnir')";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidOperationException("PostgreSQL contains tables without the fresh-install baseline. Refusing to adopt existing state.");
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = Sql; command.ExecuteNonQuery();
            command.CommandText = "CREATE TABLE public.froststream_installation_baseline(version integer PRIMARY KEY,checksum text NOT NULL); INSERT INTO public.froststream_installation_baseline VALUES(1,@checksum)";
            command.Parameters.AddWithValue("checksum", Checksum); command.ExecuteNonQuery(); command.Parameters.Clear();
            // Cron schedules must be relative to installation time, not baseline capture time.
            command.CommandText = "SELECT id,cron,timezone FROM scheduling.scheduled_tasks WHERE cron IS NOT NULL";
            var schedules = new List<(int Id, DateTimeOffset? Next)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) schedules.Add((reader.GetInt32(0), CronExpression.Parse(reader.GetString(1))
                    .WithTimeZone(TimeZoneInfo.FindSystemTimeZoneById(reader.GetString(2))).GetNextValidTimeAfter(DateTimeOffset.UtcNow)));
            foreach (var schedule in schedules)
            {
                command.CommandText = "UPDATE scheduling.scheduled_tasks SET next_due_at=@next WHERE id=@id";
                command.Parameters.Clear(); command.Parameters.AddWithValue("id", schedule.Id);
                command.Parameters.AddWithValue("next", (object?)schedule.Next ?? DBNull.Value); command.ExecuteNonQuery();
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
