# FluentMigrator Commands & Guide

This document provides a reference for working with FluentMigrator migrations in the DataBridge service.

## 🚀 Quick Reference

### Automatic Migrations (Recommended)
Migrations run automatically when the application starts:
```bash
cd src/Services/DataBridge
dotnet run
```

The application will:
1. Connect to the database
2. Check which migrations have been applied (via `versioninfo` table)
3. Run any pending migrations
4. Start the service

## 📝 Creating Migrations

### 1. Create a New Migration File

Create a new file in `Migrations/FluentMigrator/` with a descriptive name:

```csharp
using FluentMigrator;

namespace DataBridge.Migrations.FluentMigrator;

[Migration(2)] // Use next sequential number
public class AddUserProfileTable : Migration
{
    public override void Up()
    {
        Create.Table("user_profiles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
                .ForeignKey("FK_user_profiles_users", "users", "id")
            .WithColumn("bio").AsString(1000).Nullable()
            .WithColumn("avatar_url").AsString(500).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("user_profiles");
    }
}
```

### 2. Migration Numbering

Migrations use the `[Migration(X)]` attribute for versioning:
- Use sequential integers: 1, 2, 3, 4...
- Or use timestamps: 20260207001, 20260207002...
- Numbers must be unique across all migrations

**Current migrations:**
- `001_CreateStorageConfigsTable.cs` - Migration 1
- `002_CreateInitialVersionedSchema.cs` - Migration 2
- `003_AddPendingJobLinksTable.cs` - Migration 3

### 3. Run the Migration

Simply start the application:
```bash
dotnet run
```

Or build and run from the AppHost:
```bash
cd ../../AppHost
dotnet run
```

## 🔧 Manual Migration Commands (Optional)

Install FluentMigrator CLI tool (one-time):
```bash
dotnet tool install -g FluentMigrator.DotNet.Cli
```

### Migrate Up (Apply All Pending)
```bash
dotnet fm migrate \
  -p postgres \
  -c "Host=localhost;Port=5432;Database=froststreamdb;Username=postgres;Password=postgres" \
  -a ./bin/Debug/net10.0/DataBridge.dll
```

### Migrate to Specific Version
```bash
dotnet fm migrate \
  -p postgres \
  -c "connection-string" \
  -a ./bin/Debug/net10.0/DataBridge.dll \
  --version 2
```

### Rollback (Migrate Down)
```bash
# Rollback 1 migration
dotnet fm rollback \
  -p postgres \
  -c "connection-string" \
  -a ./bin/Debug/net10.0/DataBridge.dll \
  --steps 1

# Rollback to specific version
dotnet fm rollback \
  -p postgres \
  -c "connection-string" \
  -a ./bin/Debug/net10.0/DataBridge.dll \
  --version 1
```

### List Migrations
```bash
dotnet fm list migrations \
  -p postgres \
  -c "connection-string" \
  -a ./bin/Debug/net10.0/DataBridge.dll
```

### Validate Migrations
```bash
dotnet fm validate \
  -p postgres \
  -c "connection-string" \
  -a ./bin/Debug/net10.0/DataBridge.dll
```

## 📚 Common Migration Patterns

### Create a Table
```csharp
Create.Table("table_name")
    .WithColumn("id").AsGuid().PrimaryKey()
    .WithColumn("name").AsString(200).NotNullable()
    .WithColumn("created_at").AsDateTime().NotNullable();
```

### Add a Column
```csharp
Alter.Table("users")
    .AddColumn("phone_number").AsString(20).Nullable();
```

### Add a Foreign Key
```csharp
Create.ForeignKey("FK_subtitles_movies")
    .FromTable("subtitles").ForeignColumn("movie_id")
    .ToTable("movies").PrimaryColumn("id")
    .OnDelete(System.Data.Rule.Cascade);
```

### Create an Index
```csharp
Create.Index("IX_users_email")
    .OnTable("users")
    .OnColumn("email")
    .Ascending()
    .WithOptions().Unique();
```

### Alter a Column
```csharp
Alter.Table("users")
    .AlterColumn("username").AsString(150).NotNullable();
```

### Delete a Column
```csharp
Delete.Column("old_column").FromTable("table_name");
```

### Rename a Column
```csharp
Rename.Column("old_name").OnTable("users").To("new_name");
```

### Rename a Table
```csharp
Rename.Table("old_table_name").To("new_table_name");
```

### Insert Data
```csharp
Insert.IntoTable("users")
    .Row(new
    {
        id = Guid.NewGuid(),
        username = "admin",
        email = "admin@example.com",
        created_at = DateTime.UtcNow
    });
```

### Execute Raw SQL
```csharp
Execute.Sql("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\"");
```

## 🔍 Checking Migration Status

### View Applied Migrations in Database
```sql
-- Connect to PostgreSQL
psql -U postgres -d froststreamdb

-- View migration history
SELECT * FROM versioninfo ORDER BY version;
```

### Check Which Migrations Exist in Code
```bash
# List migration files
ls -la src/Services/DataBridge/Migrations/FluentMigrator/
```

## 🐛 Troubleshooting

### Migration Already Applied
If you see "migration X has already been applied":
- The migration has already run
- Check the `versioninfo` table to confirm
- If you need to rerun it, manually delete the row from `versioninfo` (DEV ONLY)

### Duplicate Migration Number
If you see "duplicate migration version":
- Two migrations have the same `[Migration(X)]` number
- Renumber one of them to be unique

### Migration Failed Mid-Way
If a migration fails partway through:
```bash
# Check what was actually applied
psql -U postgres -d froststreamdb

# If needed, manually rollback changes and fix the migration
# Then delete the version from versioninfo
DELETE FROM versioninfo WHERE version = X;
```

### Connection String Issues
If migrations can't connect:
- Verify Postgres is running: `sudo systemctl status postgresql`
- Check connection string in `appsettings.json`
- If using Aspire, ensure AppHost is running

## 📖 Best Practices

1. **Always include Down() method** - Makes rollbacks possible
2. **One change per migration** - Keep migrations focused and atomic
3. **Test rollbacks** - Ensure Down() actually reverses Up()
4. **Use descriptive names** - `002_AddUserProfileTable` not `002_Update`
5. **Don't modify existing migrations** - Create new ones for changes
6. **Use transactions** - FluentMigrator wraps each migration in a transaction by default
7. **Version sequentially** - Use 1, 2, 3... or timestamps for clarity

## 🎯 Migration Lifecycle

1. **Development**: Create migration → Run application → Migration auto-applies
2. **Testing**: Deploy → Application starts → Migrations run → Service starts
3. **Production**: Deploy → Application starts → Migrations run → Service starts
4. **Rollback** (if needed): Stop service → Run rollback command → Restart

## 📊 Version Tracking

FluentMigrator tracks applied migrations in the `versioninfo` table:

```sql
CREATE TABLE versioninfo (
    version bigint NOT NULL,
    applied_on timestamp without time zone,
    description text
);
```

Each time a migration runs, a row is inserted with:
- `version`: The migration number from `[Migration(X)]`
- `applied_on`: When it was applied
- `description`: The migration class name
