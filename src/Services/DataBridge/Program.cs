using System.Threading.Tasks;
using DataBridge.Data;
using FluentMigrator.Runner;
using FluentStorage;
using FlySwattr.NATS.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace DataBridge;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();

        // Configure PostgreSQL with EF Core
        // This will automatically use:
        // 1. Aspire-injected connection string (if running via AppHost)
        // 2. Connection string from appsettings.json (if available)
        // 3. Default localhost connection (as fallback)
        builder.AddNpgsqlDbContext<FrostStreamDbContext>(
            connectionName: "froststreamdb",
            configureDbContextOptions: options =>
            {
                // Additional EF Core options can be configured here if needed
                // e.g., options.EnableSensitiveDataLogging() for development
            });

        // Configure FluentMigrator
        var connectionString = builder.Configuration.GetConnectionString("froststreamdb")
            ?? "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres";

        builder.Services.AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(Program).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddFluentMigratorConsole());

        builder.Services.AddEnterpriseNATSMessaging(opts =>
        {
            opts.Core.Url = builder.Configuration["NATS:Url"] ?? "nats://localhost:4222";
        });

        // Register FluentStorage provider modules
        StorageFactory.Modules.UseFtpStorage();
        StorageFactory.Modules.UseSftpStorage();
        StorageFactory.Modules.UseAwsStorage();
        StorageFactory.Modules.UseAzureBlobStorage();
        StorageFactory.Modules.UseGoogleCloudStorage();
        

        // Force ConsoleLifetime so Ctrl+C / SIGTERM triggers StopAsync on hosted services
        builder.Services.AddSingleton<IHostLifetime, ConsoleLifetime>();
        builder.Services.Configure<ConsoleLifetimeOptions>(o =>
        {
            // set true to hide "Application started/stopped" messages
            o.SuppressStatusMessages = false;
        });
        

        var app = builder.Build();

        // Run migrations on startup
        using (var scope = app.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        await app.RunAsync();  // waits until Ctrl+C or SIGTERM, then calls StopAsync() gracefully
    }
}