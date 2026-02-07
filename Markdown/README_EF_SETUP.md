# Entity Framework Core + FluentMigrator Setup - Complete

This document provides an overview of the Entity Framework Core and FluentMigrator setup for the FrostStream project.

## 📁 Project Structure

```
FrostStream/
├── src/
│   ├── AppHost/
│   │   └── AppHost.cs                      # Aspire orchestration (DataBridge registered)
│   ├── Services/
│   │   ├── Shared/
│   │   │   ├── Entities/                   # ✅ POCO entities (shared across services)
│   │   │   │   ├── User.cs
│   │   │   │   ├── Movie.cs
│   │   │   │   └── Subtitle.cs
│   │   │   └── Shared.csproj
│   │   └── DataBridge/
│   │       ├── Data/                       # ✅ DbContext & configurations
│   │       │   ├── FrostStreamDbContext.cs
│   │       │   └── Configurations/
│   │       │       ├── UserConfiguration.cs
│   │       │       ├── MovieConfiguration.cs
│   │       │       └── SubtitleConfiguration.cs
│   │       ├── Migrations/                 # ✅ FluentMigrator migrations
│   │       │   └── FluentMigrator/
│   │       │       └── 001_InitialSchema.cs
│   │       ├── Program.cs                  # ✅ EF Core + FluentMigrator + Aspire integration
│   │       ├── appsettings.json            # ✅ Fallback connection string
│   │       ├── MIGRATIONS.md               # ✅ Migration commands reference
│   │       ├── README_EF_SETUP.md          # This file
│   │       └── DataBridge.csproj
```

## 🗃️ Database Schema

### Tables Created:
- **users** - User profiles and authentication
- **movies** - Movie metadata and file references
- **subtitles** - Subtitle files linked to movies
- **user_favorite_movies** - Many-to-many join table

### Relationships:
- User ↔ Movie (many-to-many via favorites)
- Movie → Subtitle (one-to-many, cascade delete)

### Naming Convention:
- Tables: lowercase with underscores (`user_favorite_movies`)
- Columns: lowercase with underscores (`created_at`)

## 🔌 Connection String Priority

The application automatically uses connection strings in this order:

### 1. **Aspire (Recommended for Development)**
When running via the AppHost, the connection string is automatically injected:
```bash
dotnet run --project src/AppHost
```
The Postgres container will be started automatically via Aspire.

### 2. **appsettings.json**
If not running via Aspire, it falls back to `appsettings.json`:
```json
"ConnectionStrings": {
  "froststreamdb": "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres"
}
```

### 3. **Default Localhost**
Built-in fallback connection string for local development.

## 📦 NuGet Packages Installed

### DataBridge.csproj:
- `Microsoft.EntityFrameworkCore` (via Aspire package)
- `Npgsql.EntityFrameworkCore.PostgreSQL` (10.0.0) - Postgres provider for EF Core
- `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` (13.1.0) - Aspire integration
- `FluentMigrator` (6.3.0) - Migration framework
- `FluentMigrator.Runner` (6.3.0) - Migration runner
- `FluentMigrator.Runner.Postgres` (6.3.0) - PostgreSQL support for FluentMigrator

### Shared.csproj:
- `Microsoft.EntityFrameworkCore` (10.0.1) - For entity annotations

### Migration Strategy:
- **FluentMigrator** - Used for database migrations (schema changes)
- **EF Core** - Used for data access (DbContext, LINQ queries)

## 🚀 Quick Start

### 1. Start the Application (Migrations Run Automatically)

**Option A: Using Aspire (Recommended)**
```bash
cd src/AppHost
dotnet run
```
This will:
- Start PostgreSQL container
- Start DataBridge service
- Automatically run FluentMigrator migrations on startup
- Inject the connection string from Aspire

**Option B: Using Local Postgres**
Make sure Postgres is running locally, then:
```bash
cd src/Services/DataBridge
dotnet run
```
Migrations will run automatically on application startup.

### 2. Verify Setup
Check that the tables were created:
```sql
-- Connect to PostgreSQL
psql -U postgres -d froststreamdb

-- List tables
\dt

-- You should see:
-- users, movies, subtitles, user_favorite_movies, versioninfo (FluentMigrator tracking)
```

## 🛠️ Common Tasks

### Add a New Entity
1. Create the entity class in `Shared/Entities/`
2. Create a configuration in `DataBridge/Data/Configurations/`
3. Add a `DbSet` property to `FrostStreamDbContext`
4. Create a FluentMigrator migration in `Migrations/FluentMigrator/`:
   ```csharp
   using FluentMigrator;

   namespace DataBridge.Migrations.FluentMigrator;

   [Migration(2)] // Increment the version number
   public class AddNewEntity : Migration
   {
       public override void Up()
       {
           Create.Table("new_entity")
               .WithColumn("id").AsGuid().PrimaryKey()
               .WithColumn("name").AsString(200).NotNullable();
       }

       public override void Down()
       {
           Delete.Table("new_entity");
       }
   }
   ```
5. Run the application - migrations apply automatically on startup

### Modify an Existing Entity
1. Update the entity class in `Shared/Entities/`
2. Update the EF configuration if needed (for query mapping)
3. Create a FluentMigrator migration:
   ```csharp
   [Migration(3)]
   public class UpdateEntityColumn : Migration
   {
       public override void Up()
       {
           Alter.Table("existing_entity")
               .AddColumn("new_column").AsString(100).Nullable();
       }

       public override void Down()
       {
           Delete.Column("new_column").FromTable("existing_entity");
       }
   }
   ```
4. Run the application to apply changes

### Manual Migration Commands (Optional)

**Migrate to latest version:**
```bash
dotnet fm migrate -p postgres -c "Host=localhost;Database=froststreamdb;Username=postgres;Password=postgres" -a ./bin/Debug/net10.0/DataBridge.dll
```

**Rollback one migration:**
```bash
dotnet fm rollback -p postgres -c "connection-string" -a ./bin/Debug/net10.0/DataBridge.dll --steps 1
```

**List all migrations:**
```bash
dotnet fm list migrations -p postgres -c "connection-string" -a ./bin/Debug/net10.0/DataBridge.dll
```

### Reset Database (Development Only)
```bash
# Drop and recreate database in psql
psql -U postgres -c "DROP DATABASE froststreamdb;"
psql -U postgres -c "CREATE DATABASE froststreamdb;"

# Restart the application to run migrations
dotnet run
```

## 📝 Code Examples

### Using the DbContext in DataBridgeService
```csharp
public class DataBridgeService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    public DataBridgeService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Create a scope for DI
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

        // Example: Add a user
        var user = new User
        {
            Username = "testuser",
            Email = "test@example.com",
            CreatedAt = DateTime.UtcNow
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(stoppingToken);
    }
}
```

### Querying Data
```csharp
// Get all movies with their subtitles
var movies = await dbContext.Movies
    .Include(m => m.Subtitles)
    .ToListAsync();

// Get a user's favorite movies
var user = await dbContext.Users
    .Include(u => u.FavoriteMovies)
    .FirstOrDefaultAsync(u => u.Username == "testuser");
```

## 🔍 Troubleshooting

### "Build failed" during migration
```bash
dotnet build
```
Make sure the project builds successfully first.

### Migrations not running
- Check the `versioninfo` table in the database to see which migrations have been applied
- Ensure your migration class has a unique `[Migration(X)]` attribute
- Check application logs for FluentMigrator output

### Connection refused to Postgres
- If using Aspire: Make sure AppHost is running
- If using local Postgres: Make sure Postgres service is running
  ```bash
  sudo systemctl status postgresql
  ```

### Migration version conflicts
If you see "duplicate migration version" errors:
```bash
# Check what's in the database
psql -U postgres -d froststreamdb -c "SELECT * FROM versioninfo;"

# Manually remove conflicting version if needed (DEV ONLY)
psql -U postgres -d froststreamdb -c "DELETE FROM versioninfo WHERE version = X;"
```

### Trust relationship error (SSL)
Add to your connection string:
```
;Trust Server Certificate=true
```

## 📚 Additional Resources

- [FluentMigrator Documentation](https://fluentmigrator.github.io/)
- [FluentMigrator GitHub](https://github.com/fluentmigrator/fluentmigrator)
- [EF Core Documentation](https://learn.microsoft.com/en-us/ef/core/) (for data access)
- [Npgsql EF Core Provider](https://www.npgsql.org/efcore/)
- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)

## ✅ What's Next?

1. ✅ Migrations are already created and run automatically on startup
2. Start building your DataBridge service to handle database operations
3. Expose database operations via NATS messages (already configured)
4. Add new migrations as your schema evolves

## 🎯 Why FluentMigrator?

**Advantages over EF Core Migrations:**
- **Code-first migrations** with fluent API for better readability
- **Version control** - Explicit migration numbering and version tracking
- **Rollback support** - Easy to roll back migrations in production
- **Database-agnostic** - Easier to support multiple databases
- **No design-time dependencies** - Migrations run at runtime, no need for EF tools
- **Better CI/CD integration** - Migrations can be part of deployment process
- **More control** - Fine-grained control over SQL generation and execution
