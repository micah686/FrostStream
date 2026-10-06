namespace DataBridge.Messaging;

/// <summary>
/// Configuration for watch-time media access control. Bound from the <c>MediaAccess</c> configuration
/// section.
/// </summary>
public sealed class MediaAccessOptions
{
    public const string SectionName = "MediaAccess";

    /// <summary>
    /// Groups whose members bypass every watch-time restriction (per-media, provider, and age). Defaults
    /// to <c>admins</c> so administrators — and the single-user owner, who is a member of <c>admins</c> —
    /// can always watch restricted content. Set to an empty array to apply restrictions uniformly.
    /// </summary>
    public string[] AdminBypassGroups { get; set; } = ["admins"];
}
