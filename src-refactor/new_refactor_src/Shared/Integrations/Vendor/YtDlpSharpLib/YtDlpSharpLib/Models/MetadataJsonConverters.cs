using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Models;

internal sealed class MetadataTypeJsonConverter : JsonConverter<MetadataType>
{
    public override MetadataType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "video" => MetadataType.Video,
                "playlist" => MetadataType.Playlist,
                "multi_video" => MetadataType.MultiVideo,
                "url" => MetadataType.Url,
                "url_transparent" => MetadataType.UrlTransparent,
                _ => MetadataType.Unknown
            }
            : MetadataType.Unknown;

    public override void Write(Utf8JsonWriter writer, MetadataType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            MetadataType.Video => "video",
            MetadataType.Playlist => "playlist",
            MetadataType.MultiVideo => "multi_video",
            MetadataType.Url => "url",
            MetadataType.UrlTransparent => "url_transparent",
            _ => "unknown"
        });
}

internal sealed class LiveStatusJsonConverter : JsonConverter<LiveStatus>
{
    public override LiveStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "not_live" => LiveStatus.NotLive,
                "is_live" => LiveStatus.IsLive,
                "is_upcoming" => LiveStatus.IsUpcoming,
                "was_live" => LiveStatus.WasLive,
                "post_live" => LiveStatus.PostLive,
                _ => LiveStatus.Unknown
            }
            : LiveStatus.Unknown;

    public override void Write(Utf8JsonWriter writer, LiveStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            LiveStatus.NotLive => "not_live",
            LiveStatus.IsLive => "is_live",
            LiveStatus.IsUpcoming => "is_upcoming",
            LiveStatus.WasLive => "was_live",
            LiveStatus.PostLive => "post_live",
            _ => "unknown"
        });
}

internal sealed class AvailabilityJsonConverter : JsonConverter<Availability>
{
    public override Availability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "private" => Availability.Private,
                "premium_only" => Availability.PremiumOnly,
                "subscriber_only" => Availability.SubscriberOnly,
                "needs_auth" => Availability.NeedsAuth,
                "unlisted" => Availability.Unlisted,
                "public" => Availability.Public,
                _ => Availability.Unknown
            }
            : Availability.Unknown;

    public override void Write(Utf8JsonWriter writer, Availability value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            Availability.Private => "private",
            Availability.PremiumOnly => "premium_only",
            Availability.SubscriberOnly => "subscriber_only",
            Availability.NeedsAuth => "needs_auth",
            Availability.Unlisted => "unlisted",
            Availability.Public => "public",
            _ => "unknown"
        });
}
