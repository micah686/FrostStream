using DiscUtils;

namespace StorageExtensions.Nfs;

/// <summary>
/// Connection settings for an NFSv3 export accessed directly over the network,
/// without an operating-system mount.
/// </summary>
public sealed class NfsOptions
{
    /// <summary>Server host name or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>
    /// Exported path on the server, as it appears in the server's exports table
    /// (for example <c>/srv/media</c>).
    /// </summary>
    public required string Export { get; init; }

    /// <summary>
    /// POSIX user id presented to the server. NFSv3 authenticates with AUTH_UNIX, so the
    /// server trusts this number rather than any password; it must match the ownership of
    /// the exported files. Defaults to <c>nobody</c>.
    /// </summary>
    public int UserId { get; init; } = 65534;

    /// <summary>POSIX primary group id presented to the server. Defaults to <c>nogroup</c>.</summary>
    public int GroupId { get; init; } = 65534;

    /// <summary>Optional supplementary group ids presented to the server.</summary>
    public IReadOnlyList<int> AuxiliaryGroupIds { get; init; } = [];

    /// <summary>Mode bits applied to files this store creates.</summary>
    public UnixFilePermissions NewFilePermissions { get; init; } =
        UnixFilePermissions.OwnerRead | UnixFilePermissions.OwnerWrite |
        UnixFilePermissions.GroupRead | UnixFilePermissions.OthersRead;

    /// <summary>Mode bits applied to directories this store creates.</summary>
    public UnixFilePermissions NewDirectoryPermissions { get; init; } =
        UnixFilePermissions.OwnerRead | UnixFilePermissions.OwnerWrite | UnixFilePermissions.OwnerExecute |
        UnixFilePermissions.GroupRead | UnixFilePermissions.GroupExecute |
        UnixFilePermissions.OthersRead | UnixFilePermissions.OthersExecute;

    /// <summary>
    /// Optional folder inside the export that becomes the store's root, so callers
    /// address objects relative to it.
    /// </summary>
    public string? BasePath { get; init; }
}
