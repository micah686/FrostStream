using Microsoft.Extensions.Configuration;
using DataBridge.Data;
using DataBridge.Persistence.Workflows;
using DataBridge.Persistence.Postgres;
using DataBridge.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FrostStream.ApplicationContracts;
using DataBridge.Messaging;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Shared.Deployment;

namespace DataBridge.Persistence;

public static class PersistenceRegistration
{
    /// <summary>Registers database services without messaging, workflow stores or hosted consumers.</summary>
    public static IHostApplicationBuilder AddDataBridgePersistence(this IHostApplicationBuilder builder)
    {
        if (!builder.Services.TryAddModule(typeof(PersistenceRegistration))) return builder;
        builder.AddDeployment();
        var options = PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<IWorkflowRetentionQueries, WorkflowRetentionQueries>();
        if (options.Provider == PersistenceProvider.Postgres)
        {
            var connectionString = builder.Configuration.GetConnectionString("froststreamdb")
                ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";
            builder.Services.AddPostgresPersistence(connectionString);
            builder.Services.AddSingleton(sp => new ApplicationDatabase(sp.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        }
        else
        {
            builder.Services.AddSingleton<SqliteConnectionFactory>();
            builder.Services.AddSingleton<SqliteDurableTransport>();
            builder.Services.Replace(ServiceDescriptor.Singleton<IDurableJobPublisher>(sp => sp.GetRequiredService<SqliteDurableTransport>()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IDurableJobConsumer>(sp => sp.GetRequiredService<SqliteDurableTransport>()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IWorkerJobRoutes>(sp => sp.GetRequiredService<SqliteDurableTransport>()));
            builder.Services.AddSingleton<SqliteFunctionStore>();
            builder.Services.AddSingleton(sp => new ApplicationDatabase(sp.GetRequiredService<SqliteConnectionFactory>()));
            builder.Services.AddSingleton<SqliteConnectionSetupInterceptor>();
            builder.Services.AddDbContext<DataBridgeDbContext>((sp, db) => db
                .UseSqlite(sp.GetRequiredService<SqliteConnectionFactory>().ConnectionString,
                    sqlite => sqlite.CommandTimeout(options.BusyTimeoutSeconds))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<SqliteConnectionSetupInterceptor>()));
            builder.Services.AddScoped<IApplicationSchemaInitializer, SqliteSchemaInitializer>();
        }
        return builder;
    }
}
