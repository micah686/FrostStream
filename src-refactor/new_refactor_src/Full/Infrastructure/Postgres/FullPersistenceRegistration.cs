using DataBridge.Persistence;
using DataBridge.Persistence.Postgres;
using DataBridge.Persistence.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NodaTime;
using Shared.Deployment;
namespace FrostStream.Full.Infrastructure;
public static class FullPersistenceRegistration
{
    public static IHostApplicationBuilder AddFullPersistence(this IHostApplicationBuilder builder)
    {
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Full) throw new InvalidOperationException("PostgreSQL infrastructure requires Full.");
        if (!builder.Services.TryAddModule(typeof(FullPersistenceRegistration))) return builder;
        builder.AddDeployment();
        var options = PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<IWorkflowRetentionQueries, WorkflowRetentionQueries>();
        var connectionString = builder.Configuration.GetConnectionString("froststreamdb") ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";
        builder.Services.AddPostgresPersistence(connectionString);
        builder.Services.AddSingleton(sp => new ApplicationDatabase(new PostgresConnectionFactory(sp.GetRequiredService<Npgsql.NpgsqlDataSource>())));
        builder.Services.AddSingleton<IWorkflowStoreFactory>(new PostgresWorkflowStoreFactory(connectionString));
        builder.Services.AddSingleton(new ApplicationInitializationStep("workflow schema", 20, (sp, ct) =>
        {
            ct.ThrowIfCancellationRequested(); return sp.GetRequiredService<IWorkflowStoreFactory>().Create().Initialize().WaitAsync(ct);
        }));
        builder.Services.AddSchemaInitialization();
        return builder;
    }
}
internal sealed class PostgresWorkflowStoreFactory(string connectionString) : IWorkflowStoreFactory
{
    private readonly Lazy<Cleipnir.ResilientFunctions.Storage.IFunctionStore> store = new(() => new Cleipnir.ResilientFunctions.PostgreSQL.PostgreSqlFunctionStore(
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "cleipnir,public" }.ConnectionString, "flows"));
    public Cleipnir.ResilientFunctions.Storage.IFunctionStore Create() => store.Value;
}
