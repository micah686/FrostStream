using DiscUtils.Nfs;

namespace StorageExtensions.Nfs;

/// <summary>
/// Owns the RPC connection to one NFS export and serialises access to it.
/// </summary>
/// <remarks>
/// <see cref="NfsFileSystem"/> is synchronous and drives a single RPC socket, so it is not
/// safe for concurrent use: every operation runs under <see cref="_gate"/>. Callers await
/// the gate, but the protocol work itself blocks, which is inherent to the library.
/// </remarks>
internal sealed class NfsConnection(NfsOptions options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NfsFileSystem? _fileSystem;
    private bool _disposed;

    public NfsOptions Options => options;

    /// <summary>
    /// Runs an operation against the export, connecting on first use.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<NfsFileSystem, T> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return action(Connect());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs an operation on behalf of an already-open stream. Streams are handed out while
    /// the gate is free, so their reads and writes have to re-acquire it per call.
    /// </summary>
    public T ExecuteOnOpenFile<T>(Func<T> action)
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private NfsFileSystem Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_fileSystem is not null)
        {
            return _fileSystem;
        }

        var credentials = options.AuxiliaryGroupIds.Count > 0
            ? new RpcUnixCredential(options.UserId, options.GroupId, [.. options.AuxiliaryGroupIds])
            : new RpcUnixCredential(options.UserId, options.GroupId);

        var fileSystem = new NfsFileSystem(options.Host, credentials, options.Export);
        fileSystem.NfsOptions.NewFilePermissions = options.NewFilePermissions;
        fileSystem.NfsOptions.NewDirectoryPermissions = options.NewDirectoryPermissions;

        _fileSystem = fileSystem;
        return _fileSystem;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _disposed = true;
            _fileSystem?.Dispose();
            _fileSystem = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
