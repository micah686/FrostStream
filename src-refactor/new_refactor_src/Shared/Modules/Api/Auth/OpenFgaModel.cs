using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebAPI.Auth;

/// <summary>
/// Loads the canonical FrostStream OpenFGA authorization model embedded from
/// <c>Auth/OpenFgaModel.json</c> and provides a stable content hash for immutable-model reuse.
/// </summary>
public static class OpenFgaModel
{
    private const string ResourceName = "WebAPI.Auth.OpenFgaModel.json";

    public const string SchemaVersion = "1.1";

    public static string Json { get; } = LoadJson();

    public static string ContentHash { get; } = ComputeContentHash(Json);

    public static string ComputeContentHash(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ComputeContentHash(document.RootElement);
    }

    public static string ComputeContentHash(JsonElement model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, model, isModelRoot: true);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string LoadJson()
    {
        using var stream = typeof(OpenFgaModel).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded OpenFGA authorization model '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool isModelRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (IsServerAssignedOrDefault(property, isModelRoot))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON value kind '{element.ValueKind}' in the OpenFGA model.");
        }
    }

    /// <summary>
    /// OpenFGA expands omitted optional fields when it reads a model back. These values have the
    /// same meaning as omission, so remove them before hashing; otherwise every startup writes an
    /// equivalent immutable model version.
    /// </summary>
    private static bool IsServerAssignedOrDefault(JsonProperty property, bool isModelRoot)
    {
        if (isModelRoot && (property.NameEquals("id") || property.NameEquals("created_at")))
        {
            return true;
        }

        return property.Name switch
        {
            "metadata" or "source_info" => property.Value.ValueKind == JsonValueKind.Null,
            "conditions" or "relations" => property.Value.ValueKind == JsonValueKind.Object &&
                                         !property.Value.EnumerateObject().Any(),
            "condition" or "module" or "object" => property.Value.ValueKind == JsonValueKind.String &&
                                                       property.Value.GetString() == string.Empty,
            _ => false
        };
    }
}
