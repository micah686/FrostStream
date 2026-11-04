using FluentStorage;
using NSubstitute;
using Shouldly;
using SMBLibrary;
using SMBLibrary.Client;
using StorageExtensions.Smb;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsSmbStoreTests
{
    [Test]
    public async Task Store_Uses_Injected_Session_And_Handles_Share_Root_Without_Network_Calls()
    {
        var client = Substitute.For<ISMBClient>();
        var tree = Substitute.For<ISMBFileStore>();
        using var store = new SmbStore(new SmbOptions { Host = "fake", Share = "share" },
            () => new SmbSession(client, tree));

        (await store.GetClient()).ShouldBe(tree);
        (await store.IsFileSystem()).ShouldBeTrue();
        (await store.IsSeekable()).ShouldBeTrue();
        (await store.ObjectExists("")).ShouldBeTrue();
        (await store.DirectoryExists("")).ShouldBeTrue();

        var handle = new object();
        tree.CreateFile(out Arg.Any<object>(), out Arg.Any<FileStatus>(), "folder\\item.txt",
                Arg.Any<AccessMask>(), Arg.Any<SMBLibrary.FileAttributes>(), Arg.Any<ShareAccess>(),
                Arg.Any<CreateDisposition>(), Arg.Any<CreateOptions>(), Arg.Any<SecurityContext>())
            .Returns(call =>
            {
                call[0] = handle;
                call[1] = FileStatus.FILE_OPENED;
                return NTStatus.STATUS_SUCCESS;
            });

        (await store.ObjectExists("folder/item.txt")).ShouldBeTrue();
        tree.Received(1).CloseFile(handle);
    }
}
