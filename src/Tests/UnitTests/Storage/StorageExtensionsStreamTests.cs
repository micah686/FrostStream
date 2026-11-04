using Shouldly;
using StorageExtensions.Internal;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsStreamTests
{
    [Test]
    public void RangeStream_Reads_Only_The_Requested_Window_And_Disposes_The_Source()
    {
        var source = new TrackingMemoryStream("0123456789"u8.ToArray());
        var stream = new RangeStream(source, offset: 3, length: 4);
        var buffer = new byte[8];

        stream.CanRead.ShouldBeTrue();
        stream.CanWrite.ShouldBeFalse();
        stream.CanSeek.ShouldBeTrue();
        stream.Length.ShouldBe(4);
        stream.Read(buffer, 0, buffer.Length).ShouldBe(4);
        buffer.AsSpan(0, 4).ToArray().ShouldBe("3456"u8.ToArray());
        stream.Read(buffer, 0, buffer.Length).ShouldBe(0);

        stream.Dispose();
        source.WasDisposed.ShouldBeTrue();
    }

    [Test]
    public void RangeStream_Seeks_Within_And_Beyond_The_Window()
    {
        using var source = new MemoryStream("0123456789"u8.ToArray());
        using var stream = new RangeStream(source, offset: 2, length: 5);

        stream.Seek(1, SeekOrigin.Begin).ShouldBe(1);
        stream.Position.ShouldBe(1);
        stream.Seek(2, SeekOrigin.Current).ShouldBe(3);
        stream.Seek(-1, SeekOrigin.End).ShouldBe(4);
        stream.Seek(9, SeekOrigin.Begin).ShouldBe(9);
        stream.Flush();
        Should.Throw<IOException>(() => stream.Seek(-10, SeekOrigin.Current));
        Should.Throw<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)123));
    }

    [Test]
    public void RangeStream_Is_Read_Only_And_Validates_Read_Arguments()
    {
        using var stream = new RangeStream(new MemoryStream([1, 2, 3]), 0, 3);

        Should.Throw<NotSupportedException>(() => stream.SetLength(2));
        Should.Throw<NotSupportedException>(() => stream.Write([1], 0, 1));
        Should.Throw<ArgumentNullException>(() => stream.Read(null!, 0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => stream.Read([1], -1, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => stream.Read([1], 0, 2));
    }

    [Test]
    public void RemoteFileStream_Tracks_Offsets_Length_Permissions_And_Disposal()
    {
        var stream = new MemoryRemoteFileStream("abc"u8.ToArray(), canRead: true, canWrite: true);
        var buffer = new byte[4];

        stream.CanRead.ShouldBeTrue();
        stream.CanWrite.ShouldBeTrue();
        stream.CanSeek.ShouldBeTrue();
        stream.Length.ShouldBe(3);
        stream.Read(buffer, 0, 2).ShouldBe(2);
        buffer.AsSpan(0, 2).ToArray().ShouldBe("ab"u8.ToArray());
        stream.Position.ShouldBe(2);
        stream.Seek(-1, SeekOrigin.End).ShouldBe(2);
        stream.Write("XY"u8.ToArray(), 0, 2);
        stream.Length.ShouldBe(4);
        stream.Bytes.ShouldBe("abXY"u8.ToArray());
        stream.Position = 0;
        stream.Read(buffer, 0, 0).ShouldBe(0);
        stream.Flush();
        stream.Dispose();
        Should.Throw<ObjectDisposedException>(() => stream.Read(buffer, 0, 1));
        Should.Throw<ObjectDisposedException>(() => stream.Write([1], 0, 1));
    }

    [Test]
    public void RemoteFileStream_Rejects_Invalid_Operations_And_Arguments()
    {
        var readOnly = new MemoryRemoteFileStream([1], canRead: true, canWrite: false);
        Should.Throw<NotSupportedException>(() => readOnly.Write([1], 0, 1));
        readOnly.Dispose();

        var writeOnly = new MemoryRemoteFileStream([1], canRead: false, canWrite: true);
        Should.Throw<NotSupportedException>(() => writeOnly.Read([1], 0, 1));
        Should.Throw<ArgumentNullException>(() => writeOnly.Write(null!, 0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => writeOnly.Write([1], -1, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => writeOnly.Write([1], 0, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => writeOnly.Read([1], -1, 1));
        Should.Throw<IOException>(() => writeOnly.Seek(-2, SeekOrigin.Begin));
        Should.Throw<ArgumentOutOfRangeException>(() => writeOnly.Seek(0, (SeekOrigin)123));
        Should.Throw<ArgumentOutOfRangeException>(() => writeOnly.Position = -1);
        Should.Throw<NotSupportedException>(() => writeOnly.SetLength(0));
        writeOnly.Dispose();
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class MemoryRemoteFileStream(byte[] bytes, bool canRead, bool canWrite)
        : RemoteFileStream(bytes.Length, canRead, canWrite)
    {
        private byte[] _bytes = bytes;

        public byte[] Bytes => _bytes;

        protected override int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (position >= _bytes.Length)
            {
                return 0;
            }

            var read = Math.Min(count, _bytes.Length - (int)position);
            _bytes.AsSpan((int)position, read).CopyTo(buffer.AsSpan(offset, read));
            return read;
        }

        protected override void WriteAt(long position, byte[] buffer, int offset, int count)
        {
            var end = checked((int)position + count);
            if (end > _bytes.Length)
            {
                Array.Resize(ref _bytes, end);
            }

            buffer.AsSpan(offset, count).CopyTo(_bytes.AsSpan((int)position, count));
        }
    }
}
