using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the binary downloader's JSON payloads.
/// Property names are matched explicitly via <see cref="JsonPropertyNameAttribute"/> because the
/// upstream API mixes lowercase and kebab-case keys.
/// </summary>
[JsonSerializable(typeof(FfBinariesResponse))]
[JsonSerializable(typeof(FfBinariesPlatforms))]
[JsonSerializable(typeof(FfBinariesUrls))]
internal partial class BinaryDownloaderJsonContext : JsonSerializerContext
{
}
