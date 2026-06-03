using System.Text.Json;
using NodaTime;
using Npgsql;

namespace DataBridge;

internal static class NpgsqlDataReaderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<T> GetJsonList<T>(NpgsqlDataReader reader, string name)
    {
        var value = GetNullableString(reader, name);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<T>>(value, JsonOptions) ?? [];
    }

    public static bool IsDbNull(NpgsqlDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name));

    public static Guid GetGuid(NpgsqlDataReader reader, string name)
        => reader.GetGuid(reader.GetOrdinal(name));

    public static string GetString(NpgsqlDataReader reader, string name)
        => reader.GetString(reader.GetOrdinal(name));

    public static string? GetNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static bool GetBoolean(NpgsqlDataReader reader, string name)
        => reader.GetBoolean(reader.GetOrdinal(name));

    public static int GetInt32(NpgsqlDataReader reader, string name)
        => reader.GetInt32(reader.GetOrdinal(name));

    public static int? GetNullableInt32(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static long GetInt64(NpgsqlDataReader reader, string name)
        => reader.GetInt64(reader.GetOrdinal(name));

    public static long? GetNullableInt64(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static double GetDouble(NpgsqlDataReader reader, string name)
        => reader.GetDouble(reader.GetOrdinal(name));

    public static double? GetNullableDouble(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    public static Instant GetInstant(NpgsqlDataReader reader, string name)
        => ToInstant(reader.GetDateTime(reader.GetOrdinal(name)));

    public static Instant? GetNullableInstant(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ToInstant(reader.GetDateTime(ordinal));
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
