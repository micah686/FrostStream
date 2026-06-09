using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class LocalStoragePathResolverTests
{
    [Test]
    public void Resolve_Replaces_Storage_Root_Token()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "froststream-shared-storage"));

        LocalStoragePathResolver.Resolve(
                $"{LocalStoragePathResolver.StorageRootToken}/media",
                root)
            .ShouldBe($"{Path.TrimEndingDirectorySeparator(root)}/media");
    }

    [Test]
    public void Resolve_Leaves_Explicit_Path_Unchanged()
    {
        LocalStoragePathResolver.Resolve("/mnt/storage", null).ShouldBe("/mnt/storage");
    }

    [Test]
    public void Resolve_Rejects_Missing_Storage_Root()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            LocalStoragePathResolver.Resolve(LocalStoragePathResolver.StorageRootToken, null));

        exception.Message.ShouldContain(LocalStoragePathResolver.EnvironmentVariableName);
    }

    [Test]
    public void Resolve_Rejects_Relative_Storage_Root()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            LocalStoragePathResolver.Resolve(LocalStoragePathResolver.StorageRootToken, "./data"));

        exception.Message.ShouldContain("absolute path");
    }
}
