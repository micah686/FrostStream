using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NATS.Client.Core;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Conduit.NATS;

/// <summary>
/// System.Text.Json serializer registry for every NATS message in the application.
/// Matches the App's HTTP JSON conventions: NodaTime (Tzdb) plus string enums. Supply a custom
/// <see cref="JsonSerializerOptions"/> (e.g. backed by a source-generated <see cref="JsonSerializerContext"/>)
/// to opt into AOT/source-gen for specific contracts.
/// </summary>
public sealed class JsonSerializerRegistry : INatsSerializerRegistry
{
    /// <summary>The shared options instance used to (de)serialize all messages.</summary>
    public JsonSerializerOptions Options { get; }

    public JsonSerializerRegistry(JsonSerializerOptions? options = null)
    {
        Options = options ?? CreateDefaultOptions();
    }

    /// <summary>
    /// Builds the default option set: camelCase, NodaTime (Tzdb), and string enums. When
    /// <paramref name="typeInfoResolver"/> is supplied (e.g. a source-generated <see cref="JsonSerializerContext"/>),
    /// it drives metadata for the types it covers, with reflection as a fallback for everything else.
    /// </summary>
    public static JsonSerializerOptions CreateDefaultOptions(IJsonTypeInfoResolver? typeInfoResolver = null)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        if (typeInfoResolver != null)
            options.TypeInfoResolver = JsonTypeInfoResolver.Combine(typeInfoResolver, new DefaultJsonTypeInfoResolver());
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public INatsSerialize<T> GetSerializer<T>() => new JsonTypeSerializer<T>(Options);

    public INatsDeserialize<T> GetDeserializer<T>() => new JsonTypeSerializer<T>(Options);
}

internal sealed class JsonTypeSerializer<T> : INatsSerialize<T>, INatsDeserialize<T>
{
    private readonly JsonSerializerOptions _options;

    public JsonTypeSerializer(JsonSerializerOptions options) => _options = options;

    public void Serialize(IBufferWriter<byte> bufferWriter, T value)
    {
        using var writer = new Utf8JsonWriter(bufferWriter);
        JsonSerializer.Serialize(writer, value, _options);
        writer.Flush();
    }

    public T? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var reader = new Utf8JsonReader(buffer);
        return JsonSerializer.Deserialize<T>(ref reader, _options);
    }
}
