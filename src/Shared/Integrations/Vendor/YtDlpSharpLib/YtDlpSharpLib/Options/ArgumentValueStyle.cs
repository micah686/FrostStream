namespace YtDlpSharpLib.Options;

/// <summary>
/// Indicates how a typed option property is rendered into argv tokens.
/// </summary>
public enum ArgumentValueStyle
{
    /// <summary>
    /// The flag is emitted only when the property value is <c>true</c>; no value token follows.
    /// </summary>
    Switch,

    /// <summary>
    /// The flag is emitted followed by a separate token containing the value.
    /// </summary>
    SeparateToken
}
