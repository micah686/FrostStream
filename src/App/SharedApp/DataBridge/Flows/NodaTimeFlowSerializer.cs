using System.Text;
using System.Text.Json;
using Cleipnir.ResilientFunctions.CoreRuntime.Serialization;
using Cleipnir.ResilientFunctions.Domain;
using Cleipnir.ResilientFunctions.Storage;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace DataBridge.Flows;

/// <summary>
/// Cleipnir's built-in <c>DefaultSerializer</c> uses System.Text.Json without NodaTime support, so
/// every NodaTime <see cref="Instant"/> in a persisted flow message or effect serializes to <c>{}</c>
/// and rehydrates as <c>default(Instant)</c> — the Unix epoch (1970-01-01). Our NATS contracts carry
/// Instants (message <c>OccurredAt</c>, <c>CapturedMediaMetadata.MetadataScrapeDate</c> /
/// <c>ReleaseDate</c>, comment timestamps, …), so those values were silently reset to the epoch the
/// moment a flow read a message back out of its store.
///
/// This serializer mirrors <c>DefaultSerializer</c> exactly — plain STJ, PascalCase property names,
/// message type bytes as <c>"{FullName}, {AssemblyShortName}"</c> — but registers NodaTime (Tzdb)
/// converters so Instants round-trip as ISO-8601 strings. Only Instant handling changes; every other
/// payload serializes identically, so it stays compatible with messages the default serializer wrote.
/// Exception (de)serialization is delegated to the default implementation because it deals with
/// Cleipnir-internal types.
/// </summary>
internal sealed class NodaTimeFlowSerializer : ISerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Tolerate the PascalCase JSON already written by DefaultSerializer for in-flight flows.
            PropertyNameCaseInsensitive = true
        };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }

    public byte[] Serialize<T>(T? value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public byte[] Serialize(object? value, Type type)
        => JsonSerializer.SerializeToUtf8Bytes(value, type, Options);

    public T Deserialize<T>(byte[] bytes)
        => JsonSerializer.Deserialize<T>(bytes, Options)!;

    public SerializedMessage SerializeMessage(object message, Type messageType)
        => new(
            JsonSerializer.SerializeToUtf8Bytes(message, messageType, Options),
            Encoding.UTF8.GetBytes(SimpleQualifiedName(messageType)));

    public object DeserializeMessage(byte[] json, byte[] type)
    {
        var typeName = Encoding.UTF8.GetString(type);
        var messageType = Type.GetType(typeName)
            ?? throw new InvalidOperationException($"Unable to resolve flow message type '{typeName}'.");
        return JsonSerializer.Deserialize(json, messageType, Options)!;
    }

    public StoredException SerializeException(FatalWorkflowException fatalWorkflowException)
        => DefaultSerializer.Instance.SerializeException(fatalWorkflowException);

    public FatalWorkflowException DeserializeException(FlowId flowId, StoredException storedException)
        => DefaultSerializer.Instance.DeserializeException(flowId, storedException);

    // Matches Cleipnir's internal TypeHelper.SimpleQualifiedName: "Namespace.Type, AssemblyShortName".
    private static string SimpleQualifiedName(Type type)
        => $"{type.FullName}, {type.Assembly.GetName().Name}";
}
