using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataBridge.Persistence.Sqlite;
using NodaTime;

namespace DataBridge.Persistence;

public enum ApplicationParameterType { Text, Integer, Int64, Guid, TextArray }

public static class ApplicationParameters
{
    private static readonly ConditionalWeakTable<DbParameterCollection, PersistenceDialect> Providers = new();
    internal static void Bind(DbParameterCollection parameters, PersistenceDialect dialect) { Providers.Remove(parameters); Providers.Add(parameters, dialect); }
    private static PersistenceDialect Dialect(DbParameterCollection parameters) => Providers.TryGetValue(parameters, out var dialect) ? dialect
        : throw new InvalidOperationException("Create provider-neutral commands through ApplicationDbCommands.");
    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, object? value)
    {
        var dialect = Dialect(parameters);
        var parameter = dialect.Parameter(name, null, dialect.Provider == PersistenceProvider.Sqlite ? SqliteValue(value) : PostgresValue(value));
        parameters.Add(parameter);
        return parameter;
    }
    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, ApplicationParameterType type, object? value)
    {
        var parameter = parameters.Add(name, type); parameter.Value = value; return parameter.Parameter;
    }
    public static ApplicationParameter Add(this DbParameterCollection parameters, string name, ApplicationParameterType type)
    {
        var dialect = Dialect(parameters);
        var parameter = dialect.Parameter(name, type, DBNull.Value);
        parameters.Add(parameter);
        return new ApplicationParameter(parameter, dialect.Provider == PersistenceProvider.Sqlite);
    }

    public sealed class ApplicationParameter(DbParameter parameter, bool sqlite)
    {
        public DbParameter Parameter => parameter;
        public object? Value { get => parameter.Value; set => parameter.Value = sqlite ? SqliteValue(value) : PostgresValue(value); }
    }

    internal static object PostgresValue(object? value) => value switch
    {
        null => DBNull.Value,
        Instant instant => instant.ToDateTimeUtc(),
        LocalDate date => date.ToDateTimeUnspecified(),
        _ => value
    };

    internal static object SqliteValue(object? value) => value switch
    {
        null or DBNull => DBNull.Value,
        Guid guid => guid.ToString("N"),
        Instant instant => SqliteStorageEncoding.ToUnixMicroseconds(instant),
        DateTimeOffset timestamp => SqliteStorageEncoding.ToUnixMicroseconds(Instant.FromDateTimeOffset(timestamp)),
        DateTime timestamp => SqliteStorageEncoding.ToUnixMicroseconds(Instant.FromDateTimeUtc(timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : timestamp.ToUniversalTime())),
        LocalDate date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool boolean => boolean ? 1 : 0,
        Enum enumeration => EnumLabel(enumeration),
        byte[] bytes => bytes,
        string text => text,
        IEnumerable sequence => JsonSerializer.Serialize(sequence.Cast<object?>().Select(item => item is null ? null : SqliteValue(item)).ToArray()),
        _ => value
    };

    private static string EnumLabel(Enum value)
    {
        var name = Enum.GetName(value.GetType(), value) ?? throw new InvalidOperationException($"Unknown enum value {value}.");
        var field = value.GetType().GetField(name)!;
        return PersistenceNames.SnakeCase(name);
    }
}
