using System.Globalization;
using System.Text;
using static DataBridge.Search.TypesenseSearchHelpers;

namespace DataBridge.Search;

/// <summary>The result of splitting a raw search string into free text and Typesense filter clauses.</summary>
public sealed record ParsedQuery(string FreeText, IReadOnlyList<string> FilterParts)
{
    /// <summary>The query string to pass to Typesense; <c>*</c> when only operators were supplied.</summary>
    public string EffectiveQuery => string.IsNullOrWhiteSpace(FreeText) ? "*" : FreeText;

    public string? FilterBy => FilterParts.Count == 0 ? null : string.Join(" && ", FilterParts);

    public bool HasFilters => FilterParts.Count > 0;

    public bool HasFreeText => !string.IsNullOrWhiteSpace(FreeText);
}

/// <summary>
/// Parses advanced search syntax such as <c>channel:LinusTechTips codec:h264 after:2023 graphics card</c>
/// into a free-text query plus structured Typesense <c>filter_by</c> clauses. Quoted values are
/// supported (<c>channel:"Linus Tech Tips"</c>). Unknown <c>field:value</c> tokens are treated as
/// free text so the parser never rejects input.
/// </summary>
public static class AdvancedQueryParser
{
    public static ParsedQuery Parse(string? raw)
    {
        var freeText = new List<string>();
        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var token in Tokenize(raw))
            {
                if (TryParseOperator(token, out var filter))
                {
                    if (!string.IsNullOrEmpty(filter))
                        filters.Add(filter);
                }
                else
                {
                    freeText.Add(token);
                }
            }
        }

        return new ParsedQuery(string.Join(' ', freeText), filters);
    }

    private static bool TryParseOperator(string token, out string? filter)
    {
        filter = null;

        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return false;

        var field = token[..colon].ToLowerInvariant();
        var value = token[(colon + 1)..].Trim();

        switch (field)
        {
            case "channel":
            case "creator":
            case "uploader":
                filter = ChannelFilter(value);
                return true;
            case "platform":
                filter = ExactOrNull("platform", value);
                return true;
            case "tag":
                filter = ExactOrNull("tags", value);
                return true;
            case "category":
                filter = ExactOrNull("categories", value);
                return true;
            case "genre":
                filter = ExactOrNull("genres", value);
                return true;
            case "artist":
                filter = ExactOrNull("artists", value);
                return true;
            case "lang":
            case "language":
            case "subtitle":
                filter = ExactOrNull("caption_languages", value);
                return true;
            case "codec":
                filter = CodecFilter(value);
                return true;
            case "resolution":
            case "res":
                filter = ResolutionFilter(value);
                return true;
            case "hdr":
                filter = HdrFilter(value);
                return true;
            case "audio":
            case "channels":
                filter = AudioChannelsFilter(value);
                return true;
            case "before":
                filter = DateFilter(value, before: true);
                return true;
            case "after":
                filter = DateFilter(value, before: false);
                return true;
            case "duration":
                filter = NumericRange("duration_seconds", value);
                return true;
            case "views":
            case "view_count":
                filter = NumericRange("view_count", value);
                return true;
            case "likes":
            case "like_count":
                filter = NumericRange("like_count", value);
                return true;
            default:
                return false;
        }
    }

    private static string? ExactOrNull(string field, string value)
        => string.IsNullOrWhiteSpace(value) ? null : Eq(field, value);

    private static string? ChannelFilter(string value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : "(" + Eq("account_handle", value) + " || " + Eq("account_name", value) + ")";

    private static string? CodecFilter(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var codec = value.Trim().ToLowerInvariant() switch
        {
            "h265" or "x265" => "hevc",
            "x264" => "h264",
            _ => value.Trim()
        };

        return "(" + Eq("video_codec", codec) + " || " + Eq("audio_codec", codec) + ")";
    }

    private static string? ResolutionFilter(string value)
    {
        var label = value.Trim().ToLowerInvariant() switch
        {
            "2160p" or "2160" or "4k" or "uhd" => "2160p",
            "1440p" or "1440" or "2k" or "qhd" => "1440p",
            "1080p" or "1080" or "fhd" => "1080p",
            "720p" or "720" or "hd" => "720p",
            "480p" or "480" => "480p",
            "sd" => "SD",
            _ => value.Trim()
        };

        return string.IsNullOrWhiteSpace(label) ? null : Eq("resolution_label", label);
    }

    private static string? HdrFilter(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "hdr" or "hdr10" or "dv" or "dolbyvision" => Ne("hdr_type", "SDR"),
            "false" or "no" or "0" or "sdr" => Eq("hdr_type", "SDR"),
            _ => null
        };

    private static string? AudioChannelsFilter(string value)
    {
        var channels = value.Trim().ToLowerInvariant() switch
        {
            "mono" => 1,
            "stereo" or "2.0" => 2,
            "2.1" => 3,
            "5.1" => 6,
            "7.1" => 8,
            _ => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0
        };

        return channels <= 0 ? null : Eq("audio_channels", channels);
    }

    private static string? DateFilter(string value, bool before)
    {
        var unix = ParseDateToUnix(value);
        if (unix is null)
            return null;

        return before
            ? $"release_date_unix:<{unix.Value}"
            : $"release_date_unix:>{unix.Value}";
    }

    private static long? ParseDateToUnix(string value)
    {
        value = value.Trim();

        if (value.Length == 4
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && year is >= 1900 and <= 2999)
        {
            return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.ToUnixTimeSeconds();
        }

        return null;
    }

    private static string? NumericRange(string field, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return null;

        var (op, rest) = value switch
        {
            _ when value.StartsWith(">=", StringComparison.Ordinal) => (">=", value[2..]),
            _ when value.StartsWith("<=", StringComparison.Ordinal) => ("<=", value[2..]),
            _ when value.StartsWith(">", StringComparison.Ordinal) => (">", value[1..]),
            _ when value.StartsWith("<", StringComparison.Ordinal) => ("<", value[1..]),
            _ when value.StartsWith("=", StringComparison.Ordinal) => ("=", value[1..]),
            _ => (">=", value) // bare number means "at least"
        };

        if (!long.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return null;

        var comparator = op == "=" ? ":=" : ":" + op;
        return field + comparator + number.ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(ch);
        }

        if (builder.Length > 0)
            tokens.Add(builder.ToString());

        return tokens;
    }
}
