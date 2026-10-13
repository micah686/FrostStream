namespace YtDlpSharpLib.Options;

/// <summary>
/// Stores the exact yt-dlp token for generated enum members whose CLI value is not simple PascalCase.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class YtDlpEnumValueAttribute(string value) : Attribute
{
    /// <summary>The exact value token to render for the enum member.</summary>
    public string Value { get; } = value;
}
