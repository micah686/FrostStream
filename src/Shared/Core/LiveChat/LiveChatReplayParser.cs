using System.Text.Json;

namespace Shared.LiveChat;

/// <summary>
/// Streaming parser for yt-dlp <c>live_chat.json</c> sidecars (JSONL: one
/// <c>replayChatItemAction</c> envelope per line). Unknown or malformed lines return null —
/// chat archives regularly contain renderer types we don't model (tickers, banners, polls) and
/// a skipped line must never fail an ingest.
/// </summary>
public static class LiveChatReplayParser
{
    public static LiveChatParsedMessage? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return ParseEnvelope(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LiveChatParsedMessage? ParseEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("replayChatItemAction", out var replay))
        {
            return null;
        }

        var offsetMs = TryGetLong(replay, "videoOffsetTimeMsec") ?? 0;

        if (!replay.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var action in actions.EnumerateArray())
        {
            if (!action.TryGetProperty("addChatItemAction", out var add) ||
                !add.TryGetProperty("item", out var item))
            {
                continue;
            }

            var parsed = ParseItem(item, offsetMs);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static LiveChatParsedMessage? ParseItem(JsonElement item, long offsetMs)
    {
        if (item.TryGetProperty("liveChatTextMessageRenderer", out var text))
        {
            return ParseCommon(text, offsetMs, LiveChatMessageType.Message, ParseRuns(text, "message"));
        }

        if (item.TryGetProperty("liveChatPaidMessageRenderer", out var paid))
        {
            return ParseCommon(paid, offsetMs, LiveChatMessageType.SuperChat, ParseRuns(paid, "message")) is { } message
                ? message with
                {
                    AmountText = GetText(paid, "purchaseAmountText"),
                    HeaderColor = TryGetColor(paid, "headerBackgroundColor"),
                    BodyColor = TryGetColor(paid, "bodyBackgroundColor"),
                }
                : null;
        }

        if (item.TryGetProperty("liveChatMembershipItemRenderer", out var membership))
        {
            var fragments = ParseRuns(membership, "message");
            if (fragments.Count == 0)
            {
                fragments = ParseRuns(membership, "headerSubtext");
            }
            if (fragments.Count == 0)
            {
                fragments = ParseRuns(membership, "headerPrimaryText");
            }

            return ParseCommon(membership, offsetMs, LiveChatMessageType.Membership, fragments);
        }

        if (item.TryGetProperty("liveChatPaidStickerRenderer", out var sticker))
        {
            return ParseCommon(sticker, offsetMs, LiveChatMessageType.Sticker, ParseStickerFragments(sticker)) is { } message
                ? message with
                {
                    AmountText = GetText(sticker, "purchaseAmountText"),
                    BodyColor = TryGetColor(sticker, "backgroundColor"),
                }
                : null;
        }

        if (item.TryGetProperty("liveChatViewerEngagementMessageRenderer", out var engagement))
        {
            return ParseCommon(engagement, offsetMs, LiveChatMessageType.System, ParseRuns(engagement, "message"));
        }

        return null;
    }

    private static LiveChatParsedMessage? ParseCommon(
        JsonElement renderer,
        long offsetMs,
        LiveChatMessageType type,
        IReadOnlyList<LiveChatFragment> fragments)
    {
        var id = TryGetString(renderer, "id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new LiveChatParsedMessage
        {
            MessageId = id,
            VideoOffsetMs = offsetMs,
            TimestampUsec = TryGetLong(renderer, "timestampUsec"),
            Type = type,
            AuthorExternalId = TryGetString(renderer, "authorExternalChannelId") ?? "",
            AuthorName = GetText(renderer, "authorName") ?? "",
            Badges = ParseBadges(renderer),
            Fragments = fragments,
        };
    }

    private static IReadOnlyList<LiveChatFragment> ParseRuns(JsonElement renderer, string property)
    {
        if (!renderer.TryGetProperty(property, out var container) ||
            !container.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fragments = new List<LiveChatFragment>();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("emoji", out var emoji))
            {
                fragments.Add(ParseEmojiRun(emoji));
                continue;
            }

            if (TryGetString(run, "text") is not { Length: > 0 } textValue)
            {
                continue;
            }

            var url = run.TryGetProperty("navigationEndpoint", out var nav) &&
                      nav.TryGetProperty("urlEndpoint", out var urlEndpoint)
                ? TryGetString(urlEndpoint, "url")
                : null;

            fragments.Add(url is null
                ? LiveChatFragment.ForText(textValue)
                : LiveChatFragment.ForLink(textValue, url));
        }

        return fragments;
    }

    private static LiveChatFragment ParseEmojiRun(JsonElement emoji)
    {
        var emojiId = TryGetString(emoji, "emojiId") ?? "";
        var isCustom = emoji.TryGetProperty("isCustomEmoji", out var custom) &&
                       custom.ValueKind == JsonValueKind.True;

        if (!isCustom)
        {
            return LiveChatFragment.ForEmoji(emojiId);
        }

        var name = emojiId;
        if (emoji.TryGetProperty("shortcuts", out var shortcuts) &&
            shortcuts.ValueKind == JsonValueKind.Array &&
            shortcuts.GetArrayLength() > 0 &&
            shortcuts[0].ValueKind == JsonValueKind.String)
        {
            name = shortcuts[0].GetString() ?? emojiId;
        }

        return LiveChatFragment.ForEmote(emojiId, name, GetLargestThumbnailUrl(emoji, "image") ?? "");
    }

    private static IReadOnlyList<LiveChatFragment> ParseStickerFragments(JsonElement renderer)
    {
        var id = TryGetString(renderer, "id") ?? "";
        var url = GetLargestThumbnailUrl(renderer, "sticker");
        if (url is null)
        {
            return [];
        }

        var label = renderer.TryGetProperty("sticker", out var sticker) &&
                    sticker.TryGetProperty("accessibility", out var accessibility) &&
                    accessibility.TryGetProperty("accessibilityData", out var data)
            ? TryGetString(data, "label") ?? "sticker"
            : "sticker";

        return [LiveChatFragment.ForEmote($"sticker:{id}", label, url)];
    }

    private static IReadOnlyList<string> ParseBadges(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("authorBadges", out var badges) ||
            badges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var badge in badges.EnumerateArray())
        {
            if (badge.TryGetProperty("liveChatAuthorBadgeRenderer", out var badgeRenderer) &&
                TryGetString(badgeRenderer, "tooltip") is { Length: > 0 } tooltip)
            {
                result.Add(tooltip);
            }
        }

        return result;
    }

    private static string? GetLargestThumbnailUrl(JsonElement renderer, string imageProperty)
    {
        if (!renderer.TryGetProperty(imageProperty, out var image) ||
            !image.TryGetProperty("thumbnails", out var thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? bestUrl = null;
        var bestWidth = -1L;
        foreach (var thumbnail in thumbnails.EnumerateArray())
        {
            var url = TryGetString(thumbnail, "url");
            if (url is null)
            {
                continue;
            }

            var width = TryGetLong(thumbnail, "width") ?? 0;
            if (width > bestWidth)
            {
                bestWidth = width;
                bestUrl = url;
            }
        }

        return bestUrl;
    }

    /// <summary>Reads either <c>{"simpleText": "…"}</c> or <c>{"runs": […]}</c> text containers.</summary>
    private static string? GetText(JsonElement renderer, string property)
    {
        if (!renderer.TryGetProperty(property, out var container))
        {
            return null;
        }

        if (TryGetString(container, "simpleText") is { } simple)
        {
            return simple;
        }

        if (!container.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = string.Concat(runs.EnumerateArray()
            .Select(static run => TryGetString(run, "text") ?? ""));
        return text.Length > 0 ? text : null;
    }

    private static string? TryGetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? TryGetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static uint? TryGetColor(JsonElement element, string property)
        => TryGetLong(element, property) is { } value ? unchecked((uint)value) : null;
}
