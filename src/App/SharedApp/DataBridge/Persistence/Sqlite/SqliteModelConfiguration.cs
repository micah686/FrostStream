using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;

namespace DataBridge.Persistence.Sqlite;

internal static class SqliteModelConfiguration
{
    private static readonly ValueConverter<Instant, long> InstantConverter = new(
        value => SqliteStorageEncoding.ToUnixMicroseconds(value), value => SqliteStorageEncoding.FromUnixMicroseconds(value));
    private static readonly ValueConverter<Guid, string> GuidConverter = new(
        value => value.ToString("N"), value => Guid.ParseExact(value, "N"));

    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var schema = entity.GetSchema() ?? throw new InvalidOperationException($"Missing logical schema for {entity.Name}.");
            var table = entity.GetTableName()!;
            entity.SetTableName(SqliteStorageEncoding.TableName(schema, table));
            entity.SetSchema(null);
            foreach (var index in entity.GetIndexes())
                if (index.GetDatabaseName() is { } name)
                    index.SetDatabaseName(SqliteStorageEncoding.TableName(schema, name));

            foreach (var check in entity.GetCheckConstraints().ToArray())
            {
                if (!check.Name!.EndsWith("_key_format", StringComparison.Ordinal)) continue;
                entity.RemoveCheckConstraint(check.Name!);
                entity.AddCheckConstraint(check.Name!, "length(\"key\") BETWEEN 2 AND 100 AND \"key\" NOT GLOB '*[^a-z0-9-]*'");
            }

            foreach (var property in entity.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                var hint = property.FindAnnotation(PersistenceModelConfiguration.TypeHint)?.Value as string;
                if (clrType == typeof(Instant))
                {
                    property.SetValueConverter(InstantConverter);
                    property.SetColumnType("INTEGER");
                }
                else if (clrType == typeof(Guid))
                {
                    property.SetValueConverter(GuidConverter);
                    property.SetColumnType("TEXT");
                }
                else if (hint is not null && SqliteBaseline.EnumLabels.TryGetValue(hint, out var expectedLabels))
                {
                    if (!clrType.IsEnum) throw new InvalidOperationException($"Expected enum for {entity.Name}.{property.Name}.");
                    var labelsType = typeof(SqliteEnumLabels<>).MakeGenericType(clrType);
                    var labels = (string[])labelsType.GetMethod("AllLabels")!.Invoke(null, null)!;
                    if (!labels.SequenceEqual(expectedLabels.Order(StringComparer.Ordinal)))
                        throw new InvalidOperationException($"Enum labels for {hint} differ from SQLite baseline v1; add a migration.");
                    property.SetValueConverter((ValueConverter)Activator.CreateInstance(typeof(SqliteEnumConverter<>).MakeGenericType(clrType))!);
                    property.SetColumnType("TEXT");
                }
                else if (hint == "jsonb")
                    property.SetColumnType("TEXT");
                else if (hint is not null)
                    throw new InvalidOperationException($"No SQLite mapping for logical type '{hint}'.");

                if (property.FindAnnotation(PersistenceModelConfiguration.TimestampDefaultHint)?.Value is true)
                    property.SetDefaultValueSql(SqliteBaseline.CurrentTimestampSql);
            }
        }
    }
}
