using Shouldly;
using StorageExtensions.Internal;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageExtensionsPathTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("  ")]
    [Arguments("/")]
    [Arguments("\\")]
    public void Split_Returns_No_Segments_For_Root_Or_Whitespace(string? path)
        => RemotePath.Split(path).ShouldBeEmpty();

    [Test]
    public void Split_Normalizes_Separators_And_Dot_Segments()
        => RemotePath.Split("/media\\./shows//episode.mkv/").ShouldBe(["media", "shows", "episode.mkv"]);

    [Test]
    [Arguments("../secret")]
    [Arguments("folder/../../secret")]
    [Arguments("folder\\..\\secret")]
    public void Split_Rejects_Parent_Traversal(string path)
    {
        var error = Should.Throw<ArgumentException>(() => RemotePath.Split(path));
        error.ParamName.ShouldBe("path");
    }

    [Test]
    [Arguments("folder\\nested\\file", "folder\\nested\\file")]
    [Arguments("/", "")]
    [Arguments(null, "")]
    public void ToSmb_Uses_Share_Relative_Backslash_Paths(string? path, string expected)
        => RemotePath.ToSmb(path).ShouldBe(expected);

    [Test]
    public void ToStoragePath_And_CombineStoragePath_Return_Canonical_Paths()
    {
        RemotePath.ToStoragePath(["/media", "shows\\episode.mkv"]).ShouldBe("media/shows/episode.mkv");
        RemotePath.CombineStoragePath("/media\\shows/", "episode.mkv").ShouldBe("media/shows/episode.mkv");
    }

    [Test]
    public void SplitLeaf_Returns_Parent_And_Name()
    {
        var (parent, name) = RemotePath.SplitLeaf("/media\\shows/episode.mkv");
        parent.ShouldBe(["media", "shows"]);
        name.ShouldBe("episode.mkv");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("/")]
    public void SplitLeaf_Rejects_The_Share_Root(string? path)
        => Should.Throw<ArgumentException>(() => RemotePath.SplitLeaf(path));

    [Test]
    public void ApplyBasePath_Handles_Missing_And_Configured_Base_Paths()
    {
        RemotePath.ApplyBasePath(null, "/media/file.mkv").ShouldBe("media/file.mkv");
        RemotePath.ApplyBasePath(" ", "media/file.mkv").ShouldBe("media/file.mkv");
        RemotePath.ApplyBasePath("/archive\\shows", "./episode.mkv").ShouldBe("archive/shows/episode.mkv");
        Should.Throw<ArgumentException>(() => RemotePath.ApplyBasePath("archive", "../outside"));
    }
}
