using DataBridge.Data;
using DataBridge.Persistence;
using DataBridge.Persistence.Workflows;
using DataBridge.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Shared.Deployment;
namespace FrostStream.Lite.Infrastructure;
public static class LitePersistenceRegistration
{
    public static IHostApplicationBuilder AddLitePersistence(this IHostApplicationBuilder builder)
    {
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Lite) throw new InvalidOperationException("SQLite infrastructure requires Lite.");
        if (!builder.Services.TryAddModule(typeof(LitePersistenceRegistration))) return builder;
        var options = PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        if (options.Provider != PersistenceProvider.Sqlite) throw new InvalidOperationException("Lite requires SQLite.");
        builder.AddDeployment();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<IWorkflowRetentionQueries, WorkflowRetentionQueries>();
        builder.Services.AddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<SqliteFunctionStore>();
        builder.Services.AddSingleton(sp => new ApplicationDatabase(sp.GetRequiredService<SqliteConnectionFactory>()));
        builder.Services.AddSingleton<SqliteConnectionSetupInterceptor>();
        builder.Services.AddDbContext<DataBridgeDbContext>((sp, db) => db
            .UseSqlite(sp.GetRequiredService<SqliteConnectionFactory>().ConnectionString,
                sqlite => sqlite.CommandTimeout(options.BusyTimeoutSeconds))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<SqliteConnectionSetupInterceptor>()));
        builder.Services.AddScoped<IApplicationSchemaInitializer, SqliteSchemaInitializer>();

        builder.Services.AddSingleton<IWorkflowStoreFactory, SqliteWorkflowStoreFactory>();
        builder.Services.AddSingleton(new ApplicationInitializationStep("workflow schema", 20, (sp, ct) =>
        {
            ct.ThrowIfCancellationRequested(); return sp.GetRequiredService<SqliteFunctionStore>().Initialize().WaitAsync(ct);
        }));
        builder.Services.AddSchemaInitialization();
        return builder;
    }
}
internal sealed class SqliteWorkflowStoreFactory(SqliteFunctionStore store) : IWorkflowStoreFactory
{
    public Cleipnir.ResilientFunctions.Storage.IFunctionStore Create() => store;
}
