using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using Npgsql.NameTranslation;
using NpgsqlTypes;

namespace DataBridge.Persistence.Sqlite;

public static class SqliteStorageEncoding
{
    public static string TableName(string schema, string name) => $"{schema}_{name}";

    // Floor sub-microsecond values consistently on both sides of the Unix epoch.
    public static long ToUnixMicroseconds(Instant instant)
    {
        var quotient = Math.DivRem(instant.ToUnixTimeTicks(), 10, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    public static Instant FromUnixMicroseconds(long value) => Instant.FromUnixTimeTicks(checked(value * 10));
}

internal sealed class SqliteEnumConverter<T>() : ValueConverter<T, string>(
    value => SqliteEnumLabels<T>.Encode(value), value => SqliteEnumLabels<T>.Decode(value)) where T : struct, Enum;

/// <summary>Uses the same PgName/snake-case field mapping as Npgsql, verified against the baseline catalog.</summary>
internal static class SqliteEnumLabels<T> where T : struct, Enum
{
    private static readonly IReadOnlyDictionary<T, string> Labels = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static)
        .ToDictionary(field => (T)field.GetValue(null)!, field => field.GetCustomAttribute<PgNameAttribute>()?.PgName
            ?? new NpgsqlSnakeCaseNameTranslator().TranslateMemberName(field.Name));
    private static readonly IReadOnlyDictionary<string, T> Values = Labels.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static string Encode(T value) => Labels.TryGetValue(value, out var label) ? label
        : throw new InvalidOperationException($"Unknown {typeof(T).Name} value '{value}'.");
    public static T Decode(string label) => Values.TryGetValue(label, out var value) ? value
        : throw new InvalidOperationException($"Unknown {typeof(T).Name} label '{label}'.");
    public static string[] AllLabels() => Values.Keys.Order(StringComparer.Ordinal).ToArray();
}
