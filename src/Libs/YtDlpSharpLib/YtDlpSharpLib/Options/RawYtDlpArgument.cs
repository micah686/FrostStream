using System.Globalization;
using System.Reflection;

namespace YtDlpSharpLib.Options;

/// <summary>
/// An advanced escape hatch that lets callers append unsupported yt-dlp flags
/// without weakening the typed surface area.
/// </summary>
public sealed record RawYtDlpArgument
{
    /// <summary>Creates a raw argument with a typed value formatted using invariant culture.</summary>
    public static RawYtDlpArgument Create<T>(string name, T value, bool isSensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new RawYtDlpArgument
        {
            Name = name,
            Value = FormatValue(value),
            IsSensitive = isSensitive
        };
    }

    /// <summary>Creates a raw argument with typed value tokens formatted using invariant culture.</summary>
    public static RawYtDlpArgument Create<T>(
        string name,
        T value,
        IEnumerable<T> additionalValues,
        bool isSensitive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(additionalValues);

        return new RawYtDlpArgument
        {
            Name = name,
            Value = FormatValue(value),
            Values = additionalValues
                .Select(FormatValue)
                .Where(static item => item is not null)
                .Select(static item => item!)
                .ToArray(),
            IsSensitive = isSensitive
        };
    }

    /// <summary>The yt-dlp flag (must start with <c>--</c>).</summary>
    public required string Name { get; init; }

    /// <summary>An optional value to follow the flag.</summary>
    public string? Value { get; init; }

    /// <summary>Additional value tokens to follow the flag, for advanced multi-argument options.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>
    /// Indicates the value is sensitive (cookies, tokens) and should be redacted in logs/exception messages.
    /// </summary>
    public bool IsSensitive { get; init; }

    private static string? FormatValue<T>(T value) =>
        value switch
        {
            null => null,
            string text => text,
            Enum otherEnum => FormatEnumValue(otherEnum),
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

    private static string FormatEnumValue(Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        var attribute = field?.GetCustomAttribute<YtDlpEnumValueAttribute>();

        return attribute?.Value ?? value.ToString().ToLowerInvariant();
    }
}
