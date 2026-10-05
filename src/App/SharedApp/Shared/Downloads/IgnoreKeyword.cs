namespace Shared.Downloads;

/// <summary>How an <see cref="IgnoreKeyword"/> pattern is matched against a video title.</summary>
public enum IgnoreKeywordMatchType
{
    /// <summary>Case-insensitive substring match (default).</summary>
    Substring = 0,

    /// <summary>Case-insensitive regular-expression match, evaluated with a timeout.</summary>
    Regex = 1
}

/// <summary>
/// A single per-config-set rule that suppresses videos whose title matches <see cref="Pattern"/>
/// during user-initiated channel/playlist downloads.
/// </summary>
public sealed record IgnoreKeyword
{
    public required string Pattern { get; init; }

    public IgnoreKeywordMatchType MatchType { get; init; } = IgnoreKeywordMatchType.Substring;
}
