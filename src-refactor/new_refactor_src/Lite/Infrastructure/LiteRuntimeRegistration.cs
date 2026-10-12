using DataBridge.Messaging;
using DataBridge.Persistence;
using DataBridge.Persistence.Secrets;
using DataBridge.Persistence.Sqlite;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Shared.Backups;
using Shared.Deployment;
using Shared.Messaging.Adapters;
using Shared.Secrets;

namespace FrostStream.Lite.Infrastructure;

public static class LiteRuntimeRegistration
{
    public static IHostApplicationBuilder AddLiteMessaging(this IHostApplicationBuilder builder)
    {
        RequireLite(builder);
        builder.Services.AddLocalApplicationTransport(builder.Configuration);
        var options = builder.Configuration.GetSection("Messaging:Local:Queue").Get<LocalQueueOptions>() ?? new();
        options.Validate();
        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<SqliteDurableTransport>();
        if (builder.Services.TryAddModule(typeof(LocalQueueMaintenance)))
        {
            builder.Services.AddSingleton(new ApplicationInitializationStep("local queue", 25, (sp, ct) => sp.GetRequiredService<SqliteDurableTransport>().InitializeAsync(ct)));
            builder.Services.AddHostedService<LocalQueueMaintenance>();
        }
        builder.Services.TryAddSingleton<IDurableJobPublisher>(sp => sp.GetRequiredService<SqliteDurableTransport>());
        builder.Services.TryAddSingleton<IDurableJobConsumer>(sp => sp.GetRequiredService<SqliteDurableTransport>());
        builder.Services.TryAddSingleton<IWorkerJobRoutes>(sp => sp.GetRequiredService<SqliteDurableTransport>());
        return builder;
    }

    public static IHostApplicationBuilder AddLiteSecretStore(this IHostApplicationBuilder builder)
    {
        RequireLite(builder);
        var options = PersistenceOptions.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.TryAddSingleton(LocalSecretStoreOptions.FromConfiguration(builder.Configuration, options, builder.Environment.ContentRootPath));
        builder.Services.TryAddSingleton<ISecretStore, SqliteSecretStore>();
        return builder;
    }

    public static IHostApplicationBuilder AddLiteBackupClient(this IHostApplicationBuilder builder)
    {
        RequireLite(builder);
        builder.Services.TryAddSingleton<IBackupServiceClient, SqliteBackupServiceClient>();
        return builder;
    }

    public static IHostApplicationBuilder AddLiteStagedStorage(this IHostApplicationBuilder builder)
    {
        RequireLite(builder);
        builder.Services.TryAddSingleton<Func<string, IStagedObjectStore>>(sp =>
            bucket => new SqliteStagedObjectStore(sp.GetRequiredService<SqliteConnectionFactory>(), bucket));
        return builder;
    }

    private static void RequireLite(IHostApplicationBuilder builder)
    {
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Lite)
            throw new InvalidOperationException("Local infrastructure requires Lite.");
    }
}
