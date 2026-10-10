using FluentMigrator.Runner;

namespace DataBridge.Persistence.Postgres;

internal sealed class PostgresSchemaInitializer(IMigrationRunner runner) : IApplicationSchemaInitializer
{
    public void Initialize(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runner.MigrateUp();
    }
}
