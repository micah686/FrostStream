using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shared.Downloads;

/// <summary>
/// Pure, reusable matcher that decides whether a video title should be ignored according to a
/// config set's <see cref="IgnoreKeyword"/> list. Regex evaluation is bounded by a timeout so a
/// pathological pattern can never stall a scan or playlist fan-out (ReDoS safe).
/// </summary>
public static class IgnoreKeywordMatcher
{
    /// <summary>Maximum time any single regex evaluation may take before it is treated as no-match.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions RegexFlags =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Returns the first keyword that matches <paramref name="title"/>, or <c>null</c> when none do
    /// (including when the title is blank or the keyword list is empty). A regex that fails to compile
    /// or times out is skipped rather than throwing.
    /// </summary>
    public static IgnoreKeyword? FirstMatch(string? title, IReadOnlyList<IgnoreKeyword>? keywords)
    {
        if (string.IsNullOrWhiteSpace(title) || keywords is null || keywords.Count == 0)
        {
            return null;
        }

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrEmpty(keyword.Pattern))
            {
                continue;
            }

            if (Matches(title, keyword))
            {
                return keyword;
            }
        }

        return null;
    }

    private static bool Matches(string title, IgnoreKeyword keyword)
    {
        if (keyword.MatchType == IgnoreKeywordMatchType.Substring)
        {
            return title.Contains(keyword.Pattern, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Regex.IsMatch(title, keyword.Pattern, RegexFlags, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Invalid pattern that slipped past validation — never let it break a scan.
            return false;
        }
    }

    /// <summary>
    /// Validates that <paramref name="keyword"/> is well-formed (used by config-set validation).
    /// Returns an error message, or <c>null</c> when the keyword is valid.
    /// </summary>
    public static string? Validate(IgnoreKeyword keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword.Pattern))
        {
            return "Ignore keyword pattern must not be empty.";
        }

        if (keyword.Pattern.Length > MaxPatternLength)
        {
            return $"Ignore keyword pattern must be {MaxPatternLength} characters or fewer.";
        }

        if (keyword.MatchType == IgnoreKeywordMatchType.Regex)
        {
            try
            {
                _ = Regex.IsMatch(string.Empty, keyword.Pattern, RegexFlags, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return $"Invalid regex pattern '{keyword.Pattern}': {ex.Message}";
            }
            catch (RegexMatchTimeoutException)
            {
                // A timeout against an empty input still means the pattern compiled.
            }
        }

        return null;
    }

    /// <summary>Maximum number of keywords allowed per config set.</summary>
    public const int MaxKeywordCount = 100;

    /// <summary>Maximum length of a single keyword pattern.</summary>
    public const int MaxPatternLength = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Deserializes the persisted JSON column into a keyword list (empty on null/blank/invalid).</summary>
    public static IReadOnlyList<IgnoreKeyword> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<IgnoreKeyword>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes a keyword list for persistence; returns null for an empty list.</summary>
    public static string? Serialize(IReadOnlyList<IgnoreKeyword>? keywords)
    {
        if (keywords is null || keywords.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(keywords, SerializerOptions);
    }
}
