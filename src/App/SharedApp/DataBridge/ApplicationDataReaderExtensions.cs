using System.Text.Json;
using NodaTime;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using DataBridge.Persistence.Sqlite;

namespace DataBridge;

internal static class ApplicationDataReaderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<T> GetJsonList<T>(DbDataReader reader, string name)
    {
        var value = GetNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<T>>(value, JsonOptions) ?? [];
    }

    public static bool IsDbNull(DbDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name));

    public static Guid GetGuid(DbDataReader reader, string name)
        => reader.GetGuid(reader.GetOrdinal(name));

    public static string GetString(DbDataReader reader, string name)
        => reader.GetString(reader.GetOrdinal(name));

    public static string? GetNullableString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static bool GetBoolean(DbDataReader reader, string name)
        => reader.GetBoolean(reader.GetOrdinal(name));

    public static int GetInt32(DbDataReader reader, string name)
        => reader.GetInt32(reader.GetOrdinal(name));

    public static int? GetNullableInt32(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static long GetInt64(DbDataReader reader, string name)
        => reader.GetInt64(reader.GetOrdinal(name));

    public static long? GetNullableInt64(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static double GetDouble(DbDataReader reader, string name)
        => reader.GetDouble(reader.GetOrdinal(name));

    public static double? GetNullableDouble(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    public static Instant GetInstant(DbDataReader reader, string name)
        => ToInstant(reader.GetApplicationDateTime(reader.GetOrdinal(name)));

    public static Instant? GetNullableInstant(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ToInstant(reader.GetApplicationDateTime(ordinal));
    }

    public static DateTime GetApplicationDateTime(this DbDataReader reader, int ordinal)
        => reader is SqliteDataReader ? SqliteStorageEncoding.FromUnixMicroseconds(reader.GetInt64(ordinal)).ToDateTimeUtc() : reader.GetDateTime(ordinal);

    public static T GetApplicationValue<T>(this DbDataReader reader, int ordinal)
    {
        if (reader is not SqliteDataReader) return reader.GetFieldValue<T>(ordinal);
        if (typeof(T) == typeof(DateTimeOffset)) return (T)(object)SqliteStorageEncoding.FromUnixMicroseconds(reader.GetInt64(ordinal)).ToDateTimeOffset();
        if (typeof(T) == typeof(string[])) return (T)(object)(JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)) ?? []);
        return reader.GetFieldValue<T>(ordinal);
    }

    private static Instant ToInstant(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return Instant.FromDateTimeUtc(utc);
    }
}
