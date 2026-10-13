using Conduit.NATS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Shared.Backups;
using Shared.Deployment;
using Shared.Messaging.Adapters;
using Shared.Secrets;

namespace FrostStream.Full.Infrastructure;

public static class FullInfrastructureRegistration
{
    public static IHostApplicationBuilder AddFullMessaging(this IHostApplicationBuilder builder, bool provisionTopology = true)
    {
        builder.Configuration["Deployment:Mode"] ??= "Full";
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Full)
            throw new InvalidOperationException("A Full host requires Deployment:Mode=Full.");
        if (!builder.Services.TryAddModule(typeof(FullInfrastructureRegistration))) return builder;
        builder.AddDeployment();
        builder.Services.AddNats(options =>
        {
            options.Url = builder.Configuration.GetConnectionString("nats") ?? builder.Configuration["NATS:Url"] ?? "nats://localhost:24040";
            var token = builder.Configuration["NATS:Token"];
            var username = builder.Configuration["NATS:Username"];
            var password = builder.Configuration["NATS:Password"];
            var creds = builder.Configuration["NATS:CredsFile"];
            options.AuthOpts = !string.IsNullOrWhiteSpace(token) ? new NatsAuthOpts { Token = token }
                : !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) ? new NatsAuthOpts { Username = username, Password = password }
                : !string.IsNullOrWhiteSpace(creds) ? new NatsAuthOpts { CredsFile = creds } : null;
            options.EnableTopologyProvisioning = provisionTopology;
        });
        builder.Services.AddNatsTopologySource<NatsDownloadTopology>();
        builder.Services.AddNatsTopologySource<NatsArtifactStorageTopology>();
        builder.Services.AddNatsTopologySource<NatsPlaylistTopology>();
        builder.Services.AddNatsTopologySource<NatsBackgroundJobsTopology>();
        builder.Services.AddNatsTopologySource<NatsLocalImportTopology>();
        builder.Services.AddNatsTopologySource<NatsAuthSessionsTopology>();
        builder.Services.AddNatsApplicationTransport();
        return builder;
    }

    public static IHostApplicationBuilder AddFullBackupClient(this IHostApplicationBuilder builder)
    {
        if (DeploymentOptions.FromConfiguration(builder.Configuration).Mode != DeploymentMode.Full)
            throw new InvalidOperationException("The remote backup client requires Full.");
        builder.Services.AddHttpClient<IBackupServiceClient, BackupServiceClient>(client =>
            client.BaseAddress = new Uri(builder.Configuration["BackupService:BaseUrl"] ?? "http://backupservice"));
        return builder;
    }
}
