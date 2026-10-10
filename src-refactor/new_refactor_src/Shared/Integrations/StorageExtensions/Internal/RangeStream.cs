namespace StorageExtensions.Internal;

/// <summary>
/// Exposes a fixed byte window of a seekable stream, so <c>OpenRange</c> stops at the
/// requested length instead of running to the end of the file.
/// </summary>
internal sealed class RangeStream(Stream inner, long offset, long length) : Stream
{
    private long _position;

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => false;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        ValidateBufferArguments(buffer, bufferOffset, count);

        var remaining = length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, remaining);
        inner.Position = offset + _position;
        var read = inner.Read(buffer, bufferOffset, toRead);
        _position += read;
        return read;
    }

    public override long Seek(long seekOffset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => seekOffset,
            SeekOrigin.Current => _position + seekOffset,
            SeekOrigin.End => length + seekOffset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin.")
        };

        if (target < 0)
        {
            throw new IOException("Cannot seek before the beginning of the range.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
        => throw new NotSupportedException("A range stream is read-only.");

    public override void Write(byte[] buffer, int bufferOffset, int count)
        => throw new NotSupportedException("A range stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
