using DataBridge.Persistence.Postgres;
using Npgsql;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class PostgresFreshInstallTests
{
    [Test]
    public async Task Fresh_Baseline_Is_Atomic_Repeatable_And_Rejects_Changed_History()
    {
        var adminString = Environment.GetEnvironmentVariable("FROSTSTREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminString)) return;
        var name = "phase6_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(adminString) { Database = name }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(connectionString);
            var schema = new PostgresSchemaInitializer(source); schema.Initialize(); schema.Initialize();
            await using var connection = await source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM scheduling.scheduled_tasks WHERE next_due_at>CURRENT_TIMESTAMP";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBeGreaterThan(0);
            command.CommandText = "UPDATE scheduling.scheduled_tasks SET enabled=false WHERE key='search-reindex'"; await command.ExecuteNonQueryAsync();
            schema.Initialize();
            command.CommandText = "SELECT enabled FROM scheduling.scheduled_tasks WHERE key='search-reindex'";
            (await command.ExecuteScalarAsync()).ShouldBe(false);
            command.CommandText = "UPDATE froststream_installation_baseline SET checksum='changed'"; await command.ExecuteNonQueryAsync();
            Should.Throw<InvalidOperationException>(() => schema.Initialize()).Message.ShouldContain("baseline");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
        }
    }
}
