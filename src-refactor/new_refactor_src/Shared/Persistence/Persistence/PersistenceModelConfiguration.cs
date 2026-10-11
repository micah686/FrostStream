

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

    public static void Apply(ModelBuilder modelBuilder, PersistenceDialect dialect) => dialect.ConfigureModel(modelBuilder);
}
