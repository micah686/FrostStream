using SMBLibrary.Client;

namespace StorageExtensions.Smb;

/// <summary>
/// Wire protocol to negotiate with the file server.
/// </summary>
public enum SmbDialect
{
    /// <summary>SMB 2.x / 3.x. The default, and the only dialect modern servers enable.</summary>
    Smb2,

    /// <summary>SMB 1.0 (CIFS). Only for legacy servers; disabled by default on current ones.</summary>
    Cifs
}

/// <summary>
/// Connection settings for an SMB/CIFS share accessed directly over the network,
/// without an operating-system mount.
/// </summary>
public sealed class SmbOptions
{
    /// <summary>Server host name or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>Share name on the server, without the leading <c>\\server\</c>.</summary>
    public required string Share { get; init; }

    /// <summary>
    /// Transport port. SMBLibrary supports only 445 (direct TCP, the default) and
    /// 139 (NetBIOS over TCP); any other value is rejected when the store connects.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>Authentication domain. Empty for local accounts or workgroup servers.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>User name. Leave empty for anonymous/guest access.</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Password. Leave empty for anonymous/guest access.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Dialect to negotiate. Defaults to SMB 2/3.</summary>
    public SmbDialect Dialect { get; init; } = SmbDialect.Smb2;

    /// <summary>NTLM variant used during logon. NTLMv2 is the secure default.</summary>
    public AuthenticationMethod AuthenticationMethod { get; init; } = AuthenticationMethod.NTLMv2;

    /// <summary>How long to wait for a server response before failing an operation.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether to offer SMB 3.1.1. Ignored for the CIFS dialect.</summary>
    public bool EnableSmb311 { get; init; } = true;

    /// <summary>
    /// Optional folder inside the share that becomes the store's root, so callers
    /// address objects relative to it.
    /// </summary>
    public string? BasePath { get; init; }
}
