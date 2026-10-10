namespace StorageExtensions.Nfs;

/// <summary>
/// Routes every network-touching stream operation through the connection's gate, so a
/// stream handed to a caller cannot interleave RPCs with other work on the same export.
/// </summary>
/// <remarks>
/// Position, length, and seeking are tracked client-side by the underlying NFS stream and
/// cost no round trip, so they are not gated.
/// </remarks>
internal sealed class NfsSynchronizedStream(NfsConnection connection, Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => connection.ExecuteOnOpenFile(() => inner.Read(buffer, offset, count));

    public override void Write(byte[] buffer, int offset, int count)
        => connection.ExecuteOnOpenFile<object?>(() =>
        {
            inner.Write(buffer, offset, count);
            return null;
        });

    public override void Flush()
        => connection.ExecuteOnOpenFile<object?>(() =>
        {
            inner.Flush();
            return null;
        });

    public override void SetLength(long value)
        => connection.ExecuteOnOpenFile<object?>(() =>
        {
            inner.SetLength(value);
            return null;
        });

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            connection.ExecuteOnOpenFile<object?>(() =>
            {
                inner.Dispose();
                return null;
            });
        }

        base.Dispose(disposing);
    }
}
