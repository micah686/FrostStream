namespace YtDlpSharpLib.Options;

/// <summary>
/// Root options model for a yt-dlp invocation. The generated partial declaration
/// contains the typed option groups emitted from <c>yt-dlp --help</c>.
/// </summary>
public sealed partial record YtDlpOptions
{
    /// <summary>Advanced escape hatch for unsupported yt-dlp flags.</summary>
    public IReadOnlyList<RawYtDlpArgument> AdvancedArguments { get; init; } = [];

    /// <summary>Parses yt-dlp command-line/config-file option tokens into a typed options model.</summary>
    public static YtDlpOptions FromString(IEnumerable<string> lines) =>
        YtDlpConfigFile.Parse(lines);

    /// <summary>Parses yt-dlp command-line/config-file text into a typed options model.</summary>
    public static YtDlpOptions FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var reader = new StringReader(text);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return FromString(lines);
    }

    /// <summary>Loads a yt-dlp-compatible configuration file into a typed options model.</summary>
    public static YtDlpOptions LoadConfigFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return FromString(File.ReadLines(path));
    }

    /// <summary>Renders this options model to deterministic yt-dlp argv tokens.</summary>
    public IReadOnlyList<string> GetOptionFlags() =>
        YtDlpConfigFile.RenderArguments(this);

    /// <summary>Writes this options model as a yt-dlp-compatible configuration file.</summary>
    public void WriteConfigFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        File.WriteAllLines(path, YtDlpConfigFile.RenderConfigLines(this));
    }

    /// <inheritdoc />
    public override string ToString() =>
        YtDlpConfigFile.RenderCommandLine(this);
}
