namespace YtDlpSharpLib.Options;

/// <summary>
/// Marks a property on <see cref="YtDlpOptions"/> as an option group and supplies deterministic render order.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class YtDlpOptionGroupAttribute(int order) : Attribute
{
    /// <summary>Order used by the argument renderer when visiting option groups.</summary>
    public int Order { get; } = order;
}
