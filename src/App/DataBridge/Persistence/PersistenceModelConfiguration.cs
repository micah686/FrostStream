using DataBridge.Persistence.Postgres;
using DataBridge.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataBridge.Persistence;

/// <summary>Shared configurations describe logical types; provider passes supply their storage mappings.</summary>
internal static class PersistenceModelConfiguration
{
    internal const string TypeHint = "FrostStream:PersistenceType";
    internal const string TimestampDefaultHint = "FrostStream:TimestampDefault";

    public static PropertyBuilder<T> HasPersistenceType<T>(this PropertyBuilder<T> builder, string logicalType)
        => builder.HasAnnotation(TypeHint, logicalType);

    public static PropertyBuilder<T> HasPersistenceTimestampDefault<T>(this PropertyBuilder<T> builder)
        => builder.HasAnnotation(TimestampDefaultHint, true);

    public static void Apply(ModelBuilder modelBuilder, bool sqlite)
    {
        if (sqlite)
        {
            SqliteModelConfiguration.Apply(modelBuilder);
            return;
        }
        PostgresModelConfiguration.Apply(modelBuilder);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            if (property.FindAnnotation(TypeHint)?.Value is string columnType)
                property.SetColumnType(columnType);
            if (property.FindAnnotation(TimestampDefaultHint)?.Value is true)
                property.SetDefaultValueSql("CURRENT_TIMESTAMP");
        }
    }
}
