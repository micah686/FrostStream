using SMBLibrary.Client;
using Shouldly;
using StorageExtensions.Smb;
using TUnit.Core;

namespace UnitTests.Storage;

public class Smb1FlushFileBuffersTests
{
    [Test]
    public void CreateRequest_Uses_The_Open_File_Id()
    {
        Smb1FlushFileBuffers.CreateRequest((ushort)37).FID.ShouldBe((ushort)37);
    }

    [Test]
    public void CreateRequest_Rejects_A_NonSmb1_Handle()
    {
        Should.Throw<ArgumentException>(() => Smb1FlushFileBuffers.CreateRequest(new object()));
    }

    [Test]
    public void GetTreeId_Reads_The_Id_Required_By_The_Smb1_Header()
    {
        var client = new SMB1Client();
        var tree = new SMB1FileStore(client, 91);

        Smb1FlushFileBuffers.GetTreeId(tree).ShouldBe((ushort)91);
    }
}
