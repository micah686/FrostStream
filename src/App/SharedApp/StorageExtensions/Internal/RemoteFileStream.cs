namespace StorageExtensions.Internal;

/// <summary>
/// Position bookkeeping for a stream over a remote file that is addressed by absolute
/// offset rather than by a server-side cursor. Both SMB and NFS read and write this way,
/// so seeking is free and only the transfer itself is provider-specific.
/// </summary>
internal abstract class RemoteFileStream : Stream
{
    private long _position;
    private long _length;
    private bool _disposed;

    protected RemoteFileStream(long length, bool canRead, bool canWrite)
    {
        _length = length;
        CanRead = canRead;
        CanWrite = canWrite;
    }

    public override bool CanRead { get; }
    public override bool CanWrite { get; }
    public override bool CanSeek => true;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes at <paramref name="position"/>.
    /// Returns 0 at end of file. Providers may return fewer bytes than requested.
    /// </summary>
    protected abstract int ReadAt(long position, byte[] buffer, int offset, int count);

    /// <summary>
    /// Writes exactly <paramref name="count"/> bytes at <paramref name="position"/>.
    /// </summary>
    protected abstract void WriteAt(long position, byte[] buffer, int offset, int count);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBufferArguments(buffer, offset, count);
        if (!CanRead)
        {
            throw new NotSupportedException("The stream was not opened for reading.");
        }

        if (count == 0)
        {
            return 0;
        }

        var read = ReadAt(_position, buffer, offset, count);
        _position += read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBufferArguments(buffer, offset, count);
        if (!CanWrite)
        {
            throw new NotSupportedException("The stream was not opened for writing.");
        }

        if (count == 0)
        {
            return;
        }

        WriteAt(_position, buffer, offset, count);
        _position += count;
        _length = Math.Max(_length, _position);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin.")
        };

        if (target < 0)
        {
            throw new IOException("Cannot seek before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("Remote streams cannot be resized after they are opened.");

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
