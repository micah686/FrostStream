using NSubstitute;
using Shouldly;
using SMBLibrary;
using SMBLibrary.Client;
using StorageExtensions.Smb;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsSmbFileStreamTests
{
    [Test]
    public void SmbFileStream_Reads_In_Chunks_And_Handles_End_Of_File()
    {
        var (connection, client, tree, handle) = CreateSession();
        client.MaxReadSize.Returns((uint)2);
        tree.ReadFile(out Arg.Any<byte[]>(), handle, Arg.Any<long>(), Arg.Any<int>())
            .Returns(call =>
            {
                if ((long)call[2]! >= 2)
                {
                    call[0] = Array.Empty<byte>();
                    return NTStatus.STATUS_END_OF_FILE;
                }

                call[0] = "ab"u8.ToArray();
                return NTStatus.STATUS_SUCCESS;
            });
        using var stream = new SmbFileStream(connection, handle, 2, canRead: true, canWrite: false);
        var buffer = new byte[4];

        stream.Read(buffer, 0, 2).ShouldBe(2);
        buffer[..2].ShouldBe("ab"u8.ToArray());
        stream.Read(buffer, 0, 2).ShouldBe(0);
    }

    [Test]
    public void SmbFileStream_Writes_Flushes_And_Closes_The_Handle()
    {
        var (connection, _, tree, handle) = CreateSession();
        tree.WriteFile(out Arg.Any<int>(), handle, Arg.Any<long>(), Arg.Any<byte[]>())
            .Returns(call =>
            {
                call[0] = ((byte[])call[3]!).Length;
                return NTStatus.STATUS_SUCCESS;
            });
        tree.FlushFileBuffers(handle).Returns(NTStatus.STATUS_SUCCESS);
        tree.CloseFile(handle).Returns(NTStatus.STATUS_SUCCESS);
        var stream = new SmbFileStream(connection, handle, 0, canRead: false, canWrite: true);

        stream.Write("hello"u8.ToArray(), 0, 5);
        stream.Length.ShouldBe(5);
        stream.Flush();
        stream.Dispose();
        tree.Received(2).FlushFileBuffers(handle);
        tree.Received(1).CloseFile(handle);
        Should.Throw<ObjectDisposedException>(() => stream.Flush());
    }

    private static (SmbConnection Connection, ISMBClient Client, ISMBFileStore Tree, object Handle) CreateSession()
    {
        var client = Substitute.For<ISMBClient>();
        client.MaxReadSize.Returns((uint)4096);
        client.MaxWriteSize.Returns((uint)4096);
        var tree = Substitute.For<ISMBFileStore>();
        var handle = new object();
        var connection = new SmbConnection(new SmbOptions { Host = "fake", Share = "share" },
            () => new SmbSession(client, tree));
        return (connection, client, tree, handle);
    }
}
