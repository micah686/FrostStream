namespace YtDlpSharpLib.Options;

/// <summary>
/// Maps a typed option property to the exact yt-dlp command-line flag.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class YtDlpArgumentAttribute(string name) : Attribute
{
    /// <summary>The yt-dlp flag (long form, including leading double-dashes).</summary>
    public string Name { get; } = name;

    /// <summary>Alternate short or long flags accepted by yt-dlp for the same option.</summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>The value name shown by yt-dlp help, when this option accepts a value.</summary>
    public string? ValueName { get; set; }

    /// <summary>The generated description copied from yt-dlp help.</summary>
    public string? Description { get; set; }

    /// <summary>Indicates this attribute was emitted by the yt-dlp help generator.</summary>
    public bool IsGenerated { get; set; }

    /// <summary>How the value is rendered. Defaults to <see cref="ArgumentValueStyle.SeparateToken"/>.</summary>
    public ArgumentValueStyle ValueStyle { get; init; } = ArgumentValueStyle.SeparateToken;

    /// <summary>Indicates whether the property is a collection that may emit the flag multiple times.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>
    /// Number of value tokens emitted after the flag. Values greater than one require a collection value.
    /// </summary>
    public int ValueTokenCount { get; init; } = 1;
}
