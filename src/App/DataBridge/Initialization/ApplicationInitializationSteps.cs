using Cleipnir.ResilientFunctions.PostgreSQL;
using DataBridge.LiveChat;
using DataBridge.Messaging;
using DataBridge.Search;
using FluentMigrator.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataBridge.Initialization;

public sealed class PostgresMigrationInitializationStep(
    IServiceScopeFactory scopeFactory) : IApplicationInitializationStep
{
    public string Name => "postgres-migrations";
    public IReadOnlyCollection<string> Dependencies => [];
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        // FluentMigrator exposes a synchronous runner. Isolate it so the command still observes
        // the coordinator deadline; container/process exit terminates a timed-out migration.
        await Task.Run(runner.MigrateUp, cancellationToken).WaitAsync(cancellationToken);
    }
}

public sealed class CleipnirStoreInitializationStep(IConfiguration configuration) : IApplicationInitializationStep
{
    public string Name => "cleipnir-workflow-store";
    public IReadOnlyCollection<string> Dependencies => ["postgres-migrations"];
    public TimeSpan Timeout => TimeSpan.FromMinutes(2);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("froststreamdb")
            ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";
        var cleipnirConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "cleipnir,public"
        }.ConnectionString;
        var store = new PostgreSqlFunctionStore(cleipnirConnectionString, "flows");
        await store.Initialize().WaitAsync(cancellationToken);
    }
}

public sealed class OwnerInitializationStep(SingleUserOwnerInitializer initializer) : IApplicationInitializationStep
{
    public string Name => "fixed-owner";
    public IReadOnlyCollection<string> Dependencies => ["postgres-migrations"];
    public TimeSpan Timeout => TimeSpan.FromMinutes(1);
    public Task ExecuteAsync(CancellationToken cancellationToken) => initializer.InitializeAsync(cancellationToken);
}

public sealed class PersistentDirectoriesInitializationStep(
    IOptions<InitializationOptions> options) : IApplicationInitializationStep
{
    public string Name => "persistent-directories";
    public IReadOnlyCollection<string> Dependencies => ["postgres-migrations"];
    public TimeSpan Timeout => TimeSpan.FromMinutes(1);

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(options.Value.RequiredDirectory))
            Directory.CreateDirectory(options.Value.RequiredDirectory);
        return Task.CompletedTask;
    }
}

public sealed class TypesenseCollectionsInitializationStep(
    TypesenseInitialization initialization) : IApplicationInitializationStep
{
    public string Name => "typesense-collections";
    public IReadOnlyCollection<string> Dependencies => ["postgres-migrations"];
    public TimeSpan Timeout => TimeSpan.FromMinutes(2);
    public Task ExecuteAsync(CancellationToken cancellationToken) => initialization.InitializeAsync(cancellationToken);
}

public sealed class ClickHouseInitializationStep(ClickHouseSchemaService schema) : IApplicationInitializationStep
{
    public string Name => "clickhouse-schema";
    public IReadOnlyCollection<string> Dependencies => ["postgres-migrations"];
    public TimeSpan Timeout => TimeSpan.FromMinutes(3);
    public Task ExecuteAsync(CancellationToken cancellationToken) => schema.ApplySchemaAsync(cancellationToken);
}
