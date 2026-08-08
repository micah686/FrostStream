using FluentStorage.Storage;
using StorageExtensions.Smb;

namespace FluentStorage;

/// <summary>
/// Entry points for SMB/CIFS storage, mirroring <see cref="SftpStorage"/> and the other
/// FluentStorage provider factories.
/// </summary>
/// <remarks>
/// The share is reached over the network by the SMB client itself, so it does not need to
/// be mounted by the host first.
/// </remarks>
public static class SmbStorage
{
    /// <summary>
    /// Creates a store from fully specified <see cref="SmbOptions"/>.
    /// </summary>
    public static IStore FromOptions(SmbOptions options) => new SmbStore(options);

    /// <summary>
    /// Creates an SMB 2/3 store using user name and password authentication.
    /// </summary>
    /// <param name="host">Server host name or IP address.</param>
    /// <param name="share">Share name, without the leading <c>\\server\</c>.</param>
    /// <param name="username">User name, or empty for guest access.</param>
    /// <param name="password">Password, or empty for guest access.</param>
    /// <param name="domain">Authentication domain, or empty for local accounts.</param>
    /// <param name="basePath">Optional folder inside the share to use as the store root.</param>
    public static IStore FromCredentials(
        string host,
        string share,
        string username,
        string password,
        string? domain = null,
        string? basePath = null)
        => new SmbStore(new SmbOptions
        {
            Host = host,
            Share = share,
            Username = username,
            Password = password,
            Domain = domain ?? string.Empty,
            BasePath = basePath
        });

    /// <summary>
    /// Creates an SMB 1.0 (CIFS) store, for legacy servers that do not offer SMB 2 or later.
    /// </summary>
    public static IStore FromCifsCredentials(
        string host,
        string share,
        string username,
        string password,
        string? domain = null,
        string? basePath = null)
        => new SmbStore(new SmbOptions
        {
            Host = host,
            Share = share,
            Username = username,
            Password = password,
            Domain = domain ?? string.Empty,
            Dialect = SmbDialect.Cifs,
            BasePath = basePath
        });

    /// <summary>
    /// Creates an SMB 2/3 store that logs on anonymously.
    /// </summary>
    public static IStore FromAnonymous(string host, string share, string? basePath = null)
        => new SmbStore(new SmbOptions { Host = host, Share = share, BasePath = basePath });
}
