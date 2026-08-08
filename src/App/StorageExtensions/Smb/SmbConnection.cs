using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;

namespace StorageExtensions.Smb;

/// <summary>
/// A connected client together with the tree it is bound to.
/// </summary>
internal sealed record SmbSession(ISMBClient Client, ISMBFileStore Tree);

/// <summary>
/// Owns the TCP connection, logon session, and tree connection for one share, and
/// serialises access to them.
/// </summary>
/// <remarks>
/// SMBLibrary's client is synchronous and not safe for concurrent use: a single socket
/// carries every request, so two callers interleaving writes would corrupt the stream.
/// Every operation therefore runs under <see cref="_gate"/>. Callers await the gate but
/// the protocol work itself is blocking, which is inherent to the library.
/// </remarks>
internal sealed class SmbConnection(SmbOptions options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISMBClient? _client;
    private ISMBFileStore? _tree;
    private bool _disposed;

    /// <summary>
    /// Runs an operation against the share, reconnecting once if the connection has dropped.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<SmbSession, T> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return action(Connect());
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                // The session is gone (server restart, idle timeout, network blip). Any handles
                // the caller held are void anyway, so a fresh connection is safe to retry on.
                Reset();
                return action(Connect());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs an operation that depends on an already-open file handle. A reconnect would
    /// invalidate that handle, so failures propagate instead of being retried.
    /// </summary>
    public T ExecuteOnOpenHandle<T>(Func<SmbSession, T> action)
    {
        _gate.Wait();
        try
        {
            if (_client is null || _tree is null || !_client.IsConnected)
            {
                throw new IOException("The SMB connection was lost while a file was open.");
            }

            return action(new SmbSession(_client, _tree));
        }
        finally
        {
            _gate.Release();
        }
    }

    private SmbSession Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client is not null && _tree is not null && _client.IsConnected)
        {
            return new SmbSession(_client, _tree);
        }

        Reset();

        var timeoutMs = (int)Math.Clamp(options.ResponseTimeout.TotalMilliseconds, 1000, int.MaxValue);
        ISMBClient client = options.Dialect == SmbDialect.Cifs
            ? new SMB1Client(timeoutMs)
            : new SMB2Client(timeoutMs, options.EnableSmb311);

        var transport = ResolveTransport();
        var connected = IPAddress.TryParse(options.Host, out var address)
            ? client.Connect(address, transport)
            : client.Connect(options.Host, transport);

        if (!connected)
        {
            throw new IOException(
                $"Unable to reach SMB server '{options.Host}' on {(transport == SMBTransportType.NetBiosOverTCP ? 139 : 445)}.");
        }

        try
        {
            client.Login(options.Domain, options.Username, options.Password, options.AuthenticationMethod)
                .EnsureSuccess($"logon as '{(string.IsNullOrEmpty(options.Username) ? "guest" : options.Username)}'");

            var tree = client.TreeConnect(options.Share, out var treeStatus);
            treeStatus.EnsureSuccess($"connect to share '{options.Share}'");
            if (tree is null)
            {
                throw new IOException($"SMB server returned no file store for share '{options.Share}'.");
            }

            _client = client;
            _tree = tree;
            return new SmbSession(client, tree);
        }
        catch
        {
            client.Disconnect();
            throw;
        }
    }

    private SMBTransportType ResolveTransport()
        => options.Port switch
        {
            null or 445 => SMBTransportType.DirectTCPTransport,
            139 => SMBTransportType.NetBiosOverTCP,
            _ => throw new NotSupportedException(
                $"SMB port {options.Port} is not supported; use 445 (direct TCP) or 139 (NetBIOS over TCP).")
        };

    /// <summary>
    /// Whether a failure means the session itself is unusable, as opposed to the
    /// requested operation being rejected.
    /// </summary>
    private static bool IsTransportFailure(Exception ex)
        => ex is SocketException or ObjectDisposedException
            || (ex is IOException && ex is not SmbException);

    private void Reset()
    {
        if (_tree is not null)
        {
            TryIgnore(() => _tree.Disconnect());
            _tree = null;
        }

        if (_client is not null)
        {
            TryIgnore(() => _client.Logoff());
            TryIgnore(() => _client.Disconnect());
            _client = null;
        }
    }

    /// <summary>
    /// Best-effort teardown: a connection that is already broken throws on every call,
    /// and that must not mask the error that prompted the teardown.
    /// </summary>
    private static void TryIgnore(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            Reset();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
