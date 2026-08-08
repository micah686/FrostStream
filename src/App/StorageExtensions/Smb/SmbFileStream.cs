using SMBLibrary;
using StorageExtensions.Internal;

namespace StorageExtensions.Smb;

/// <summary>
/// A stream over an open SMB file handle. Reads and writes carry an absolute offset,
/// so the stream is fully seekable without any server-side cursor.
/// </summary>
internal sealed class SmbFileStream(
    SmbConnection connection,
    object handle,
    long length,
    bool canRead,
    bool canWrite)
    : RemoteFileStream(length, canRead, canWrite)
{
    private bool _closed;

    protected override int ReadAt(long position, byte[] buffer, int offset, int count)
        => connection.ExecuteOnOpenHandle(session =>
        {
            // The server caps a single read at MaxReadSize; the caller's loop handles the rest.
            var toRead = (int)Math.Min((uint)count, session.Client.MaxReadSize);
            var status = session.Tree.ReadFile(out var data, handle, position, toRead);

            if (status == NTStatus.STATUS_END_OF_FILE)
            {
                return 0;
            }

            status.EnsureSuccess("read");
            if (data is null || data.Length == 0)
            {
                return 0;
            }

            data.CopyTo(buffer.AsSpan(offset));
            return data.Length;
        });

    protected override void WriteAt(long position, byte[] buffer, int offset, int count)
    {
        var written = 0;
        while (written < count)
        {
            // Each round trip is bounded by MaxWriteSize, and the server may still accept
            // fewer bytes than offered, so this loops until the whole chunk lands.
            var accepted = connection.ExecuteOnOpenHandle(session =>
            {
                var toWrite = (int)Math.Min((uint)(count - written), session.Client.MaxWriteSize);
                var chunk = buffer.AsSpan(offset + written, toWrite).ToArray();

                var status = session.Tree.WriteFile(out var bytesWritten, handle, position + written, chunk);
                status.EnsureSuccess("write");
                return bytesWritten;
            });

            if (accepted <= 0)
            {
                throw new IOException($"SMB server accepted no bytes while writing at offset {position + written}.");
            }

            written += accepted;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            try
            {
                connection.ExecuteOnOpenHandle(session =>
                {
                    if (CanWrite)
                    {
                        try
                        {
                            session.Tree.FlushFileBuffers(handle).EnsureSuccess("flush");
                        }
                        catch (NotImplementedException)
                        {
                            // SMBLibrary has no SMB1 flush. Closing the handle below commits
                            // the data on that dialect, so there is nothing else to do.
                        }
                    }

                    return session.Tree.CloseFile(handle);
                });
            }
            catch (IOException) when (!CanWrite)
            {
                // A read handle that cannot be closed leaks nothing the caller can act on,
                // and the server reclaims it when the session ends. A write handle is
                // different: failing to flush loses data, so that error propagates.
            }
        }

        base.Dispose(disposing);
    }
}
