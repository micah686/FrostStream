using System.Text;
using System.Text.RegularExpressions;

namespace YtDlpSharpLib;

/// <summary>General helpers that mirror small yt-dlp utility behaviors.</summary>
public static partial class YtDlpUtils
{
    /// <summary>
    /// Sanitizes a value for use as a filename, following yt-dlp's practical filename rules.
    /// </summary>
    /// <param name="value">The filename or filename fragment to sanitize.</param>
    /// <param name="restricted">When true, restricts the output to a conservative ASCII-friendly form.</param>
    public static string SanitizeFilename(string value, bool restricted = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        var result = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            AppendSanitizedCharacter(result, c, restricted);
        }

        var sanitized = result.ToString();
        if (restricted)
        {
            sanitized = RepeatedUnderscoresRegex().Replace(sanitized, "_");
        }

        sanitized = RepeatedWhitespaceRegex().Replace(sanitized, " ");
        sanitized = sanitized.Trim(' ', '.', '_');

        return sanitized.Length == 0 ? "_" : sanitized;
    }

    /// <summary>
    /// Sanitizes a value for use as a filename, following yt-dlp's practical filename rules.
    /// </summary>
    /// <param name="value">The filename or filename fragment to sanitize.</param>
    /// <param name="restricted">When true, restricts the output to a conservative ASCII-friendly form.</param>
    public static string Sanitize(string value, bool restricted = false) =>
        SanitizeFilename(value, restricted);

    private static void AppendSanitizedCharacter(StringBuilder result, char c, bool restricted)
    {
        if (char.IsControl(c) || c == '\u007f')
        {
            return;
        }

        if (restricted && c > '\u007f')
        {
            result.Append('_');
            return;
        }

        if (restricted && (char.IsWhiteSpace(c) || c is '&' or '\'' or '"' or '!' or '$' or ';' or '`' or '^' or '#'))
        {
            result.Append('_');
            return;
        }

        switch (c)
        {
            case '?' when !restricted:
                return;
            case '"' when !restricted:
                result.Append('\'');
                return;
            case ':' when !restricted:
                result.Append(" -");
                return;
            case '/' when !restricted:
                result.Append('\u29f8');
                return;
            case '\\' when !restricted:
                result.Append('\u29f9');
                return;
            case '<' or '>' or '|' or '*' or '?' or ':' or '/' or '\\':
                result.Append('_');
                return;
            default:
                result.Append(c);
                return;
        }
    }

    [GeneratedRegex("_+")]
    private static partial Regex RepeatedUnderscoresRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespaceRegex();
}
