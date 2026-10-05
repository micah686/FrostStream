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
        var guidText = modelBuilder.HasDbFunction(typeof(PersistenceFunctions).GetMethod(nameof(PersistenceFunctions.GuidText))!);
        if (sqlite) guidText.HasName("fs_guid_text");
        else guidText.HasTranslation(args => new Microsoft.EntityFrameworkCore.Query.SqlExpressions.SqlUnaryExpression(
            System.Linq.Expressions.ExpressionType.Convert, args[0], typeof(string), new Microsoft.EntityFrameworkCore.Storage.StringTypeMapping("text", System.Data.DbType.String)));
        var like = modelBuilder.HasDbFunction(typeof(PersistenceFunctions).GetMethod(nameof(PersistenceFunctions.ILike))!);
        if (sqlite) like.HasName("fs_ilike");
#pragma warning disable EF1001 // Explicit provider expression preserves PostgreSQL's existing native ILIKE behavior.
        else like.HasTranslation(args => new Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal.PgILikeExpression(args[0], args[1], args[2], null));
#pragma warning restore EF1001
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
