using FluentStorage;
using Shouldly;
using StorageExtensions.Nfs;
using StorageExtensions.Smb;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsStoreTests
{
    [Test]
    public async Task Stores_Expose_Filesystem_And_Seekable_Capabilities()
    {
        using var nfs = new NfsStore(new NfsOptions { Host = "nfs.invalid", Export = "/export" });
        using var smb = new SmbStore(new SmbOptions { Host = "smb.invalid", Share = "media" });

        (await nfs.IsFileSystem()).ShouldBeTrue();
        (await nfs.IsSeekable()).ShouldBeTrue();
        (await smb.IsFileSystem()).ShouldBeTrue();
        (await smb.IsSeekable()).ShouldBeTrue();
    }

    [Test]
    public void NfsStore_Validates_Options_And_NfsStorage_Factories_Create_Stores()
    {
        Should.Throw<ArgumentNullException>(() => new NfsStore(null!));
        Should.Throw<ArgumentException>(() => new NfsStore(new NfsOptions { Host = " ", Export = "/export" }));
        Should.Throw<ArgumentException>(() => new NfsStore(new NfsOptions { Host = "host", Export = " " }));

        using var fromOptions = NfsStorage.FromOptions(new NfsOptions { Host = "nfs.invalid", Export = "/export" });
        using var fromExport = NfsStorage.FromExport("nfs.invalid", "/export", 1200, 1300, "library");
        fromOptions.ShouldBeOfType<NfsStore>();
        fromExport.ShouldBeOfType<NfsStore>();
    }

    [Test]
    public void SmbStore_Validates_Options_And_SmbStorage_Factories_Create_Stores()
    {
        Should.Throw<ArgumentNullException>(() => new SmbStore(null!));
        Should.Throw<ArgumentException>(() => new SmbStore(new SmbOptions { Host = " ", Share = "media" }));
        Should.Throw<ArgumentException>(() => new SmbStore(new SmbOptions { Host = "host", Share = " " }));

        using var fromOptions = SmbStorage.FromOptions(new SmbOptions { Host = "smb.invalid", Share = "media" });
        using var fromCredentials = SmbStorage.FromCredentials("smb.invalid", "media", "user", "pass", "domain", "library");
        using var fromCifs = SmbStorage.FromCifsCredentials("smb.invalid", "media", "user", "pass");
        using var anonymous = SmbStorage.FromAnonymous("smb.invalid", "media");
        fromOptions.ShouldBeOfType<SmbStore>();
        fromCredentials.ShouldBeOfType<SmbStore>();
        fromCifs.ShouldBeOfType<SmbStore>();
        anonymous.ShouldBeOfType<SmbStore>();
    }
}
