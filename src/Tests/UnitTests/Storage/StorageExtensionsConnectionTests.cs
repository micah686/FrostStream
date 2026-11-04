using Shouldly;
using StorageExtensions.Nfs;
using StorageExtensions.Smb;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsConnectionTests
{
    [Test]
    public async Task NfsConnection_Executes_Open_File_Actions_And_Rejects_Canceled_Operations()
    {
        using var connection = new NfsConnection(new NfsOptions { Host = "nfs.invalid", Export = "/export" });
        connection.Options.Host.ShouldBe("nfs.invalid");
        connection.ExecuteOnOpenFile(() => 42).ShouldBe(42);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await connection.ExecuteAsync(_ => true, new CancellationToken(canceled: true)));
    }

    [Test]
    public void NfsConnection_Dispose_Is_Idempotent()
    {
        var connection = new NfsConnection(new NfsOptions { Host = "nfs.invalid", Export = "/export" });
        connection.Dispose();
        connection.Dispose();
        Should.Throw<ObjectDisposedException>(() => connection.ExecuteOnOpenFile(() => true));
    }

    [Test]
    public async Task SmbConnection_Rejects_Unsupported_Port_And_Canceled_Operations()
    {
        using var connection = new SmbConnection(new SmbOptions
        {
            Host = "127.0.0.1",
            Share = "share",
            Port = 1445
        });

        await Should.ThrowAsync<NotSupportedException>(async () => await connection.ExecuteAsync(_ => true));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await connection.ExecuteAsync(_ => true, new CancellationToken(canceled: true)));
        Should.Throw<IOException>(() => connection.ExecuteOnOpenHandle(_ => true));
    }

    [Test]
    public void SmbConnection_Dispose_Is_Idempotent_And_Prevents_Reconnect()
    {
        var connection = new SmbConnection(new SmbOptions { Host = "127.0.0.1", Share = "share" });
        connection.Dispose();
        connection.Dispose();
        Should.Throw<ObjectDisposedException>(() => connection.ExecuteAsync(_ => true).GetAwaiter().GetResult());
    }

    [Test]
    public void SmbStatuses_Map_Missing_Objects_And_Throw_For_Failures()
    {
        foreach (var status in new[]
                 {
                     SMBLibrary.NTStatus.STATUS_OBJECT_NAME_NOT_FOUND,
                     SMBLibrary.NTStatus.STATUS_OBJECT_PATH_NOT_FOUND,
                     SMBLibrary.NTStatus.STATUS_NO_SUCH_FILE,
                     SMBLibrary.NTStatus.STATUS_NOT_FOUND,
                     SMBLibrary.NTStatus.STATUS_NOT_A_DIRECTORY
                 })
        {
            status.IsNotFound().ShouldBeTrue();
        }

        SMBLibrary.NTStatus.STATUS_SUCCESS.IsNotFound().ShouldBeFalse();
        SMBLibrary.NTStatus.STATUS_SUCCESS.EnsureSuccess("probe");
        var error = Should.Throw<SmbException>(() => SMBLibrary.NTStatus.STATUS_ACCESS_DENIED.EnsureSuccess("read"));
        error.Status.ShouldBe(SMBLibrary.NTStatus.STATUS_ACCESS_DENIED);
        error.Message.ShouldContain("SMB read failed");

        var explicitError = new SmbException("write failed", SMBLibrary.NTStatus.STATUS_ACCESS_DENIED);
        explicitError.Status.ShouldBe(SMBLibrary.NTStatus.STATUS_ACCESS_DENIED);
        explicitError.Message.ShouldContain("NTStatus");
    }

    [Test]
    public async Task NfsSynchronizedStream_Forwards_Stream_Operations_Through_The_Connection()
    {
        using var connection = new NfsConnection(new NfsOptions { Host = "nfs.invalid", Export = "/export" });
        var inner = new MemoryStream();
        var stream = new NfsSynchronizedStream(connection, inner);

        stream.CanRead.ShouldBeTrue();
        stream.CanWrite.ShouldBeTrue();
        stream.CanSeek.ShouldBeTrue();
        stream.Write("hello"u8.ToArray(), 0, 5);
        stream.Flush();
        stream.Length.ShouldBe(5);
        stream.Seek(0, SeekOrigin.Begin).ShouldBe(0);
        var buffer = new byte[5];
        stream.Read(buffer, 0, 5).ShouldBe(5);
        buffer.ShouldBe("hello"u8.ToArray());
        stream.Position = 1;
        stream.SetLength(2);
        stream.Length.ShouldBe(2);
        stream.Dispose();
        inner.CanRead.ShouldBeFalse();
    }
}
