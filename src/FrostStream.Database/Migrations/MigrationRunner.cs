using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostStream.Database.Migrations;

/// <summary>
/// Handles database migration execution using FluentMigrator
/// </summary>
public class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(string connectionString, ILogger<MigrationRunner>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MigrationRunner>();
    }

    /// <summary>
    /// Run all pending migrations
    /// </summary>
    public void MigrateUp()
    {
        _logger.LogInformation("Starting database migration...");
        
        var serviceProvider = CreateServices();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        
        runner.MigrateUp();
        
        _logger.LogInformation("Database migration completed successfully");
    }

    /// <summary>
    /// Run migrations up to a specific version
    /// </summary>
    public void MigrateUp(long targetVersion)
    {
        _logger.LogInformation("Migrating database to version {Version}...", targetVersion);
        
        var serviceProvider = CreateServices();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        
        runner.MigrateUp(targetVersion);
        
        _logger.LogInformation("Database migration completed");
    }

    /// <summary>
    /// Rollback all migrations
    /// </summary>
    public void MigrateDown()
    {
        _logger.LogWarning("Rolling back all database migrations...");
        
        var serviceProvider = CreateServices();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        
        runner.MigrateDown(0);
        
        _logger.LogWarning("Database rollback completed");
    }

    /// <summary>
    /// Rollback to a specific version
    /// </summary>
    public void MigrateDown(long targetVersion)
    {
        _logger.LogWarning("Rolling back database to version {Version}...", targetVersion);
        
        var serviceProvider = CreateServices();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        
        runner.MigrateDown(targetVersion);
        
        _logger.LogInformation("Database rollback completed");
    }

    /// <summary>
    /// List all applied migrations
    /// </summary>
    public IEnumerable<long> ListAppliedMigrations()
    {
        var serviceProvider = CreateServices();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        
        return runner.MigrationLoader.LoadMigrations().Keys
            .Where(v => runner.HasMigrationsToApplyDown(v))
            .ToList();
    }

    private IServiceProvider CreateServices()
    {
        return new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations()
                .ScanIn(typeof(MigrationRunner).Assembly).For.EmbeddedResources())
            .AddLogging(lb => lb
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information))
            .BuildServiceProvider(false);
    }
}

/// <summary>
/// Extension methods for IServiceCollection to configure database migrations
/// </summary>
public static class MigrationServiceExtensions
{
    /// <summary>
    /// Add FluentMigrator services to the DI container
    /// </summary>
    public static IServiceCollection AddFrostStreamMigrations(
        this IServiceCollection services, 
        string connectionString)
    {
        services.AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations()
                .ScanIn(typeof(MigrationRunner).Assembly).For.EmbeddedResources())
            .AddLogging(lb => lb.AddFluentMigratorConsole());

        services.AddTransient<MigrationRunner>(sp => 
            new MigrationRunner(connectionString, 
                sp.GetService<ILogger<MigrationRunner>>()));

        return services;
    }

    /// <summary>
    /// Automatically run migrations on application startup
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
        runner.MigrateUp();
        
        // Refresh materialized views if needed
        await RefreshMaterializedViewsAsync(scope.ServiceProvider);
    }

    private static async Task RefreshMaterializedViewsAsync(IServiceProvider serviceProvider)
    {
        // This would typically use a DbContext to execute raw SQL
        // await dbContext.Database.ExecuteSqlRawAsync("SELECT refresh_materialized_views();");
    }
}
