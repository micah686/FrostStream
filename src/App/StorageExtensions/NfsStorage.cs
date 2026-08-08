using FluentStorage.Storage;
using StorageExtensions.Nfs;

namespace FluentStorage;

/// <summary>
/// Entry points for NFSv3 storage, mirroring <see cref="SftpStorage"/> and the other
/// FluentStorage provider factories.
/// </summary>
/// <remarks>
/// The export is reached over the network by the NFS client itself, so it does not need to
/// be mounted by the host first.
/// </remarks>
public static class NfsStorage
{
    /// <summary>
    /// Creates a store from fully specified <see cref="NfsOptions"/>.
    /// </summary>
    public static IStore FromOptions(NfsOptions options) => new NfsStore(options);

    /// <summary>
    /// Creates a store for an export, presenting the given POSIX identity to the server.
    /// </summary>
    /// <param name="host">Server host name or IP address.</param>
    /// <param name="export">Exported path, as listed in the server's exports table.</param>
    /// <param name="userId">POSIX user id to present. NFSv3 trusts this number; it must match file ownership.</param>
    /// <param name="groupId">POSIX primary group id to present.</param>
    /// <param name="basePath">Optional folder inside the export to use as the store root.</param>
    public static IStore FromExport(
        string host,
        string export,
        int userId = 65534,
        int groupId = 65534,
        string? basePath = null)
        => new NfsStore(new NfsOptions
        {
            Host = host,
            Export = export,
            UserId = userId,
            GroupId = groupId,
            BasePath = basePath
        });

    /// <summary>
    /// Lists the exports a server offers, for validating configuration before creating a store.
    /// </summary>
    public static IEnumerable<string> GetExports(string host) => NfsStore.GetExports(host);
}
