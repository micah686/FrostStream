using Shared.Imports;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Imports;

public sealed class LocalImportPathRulesTests
{
    [Test]
    public void TryNormalizeRelativePath_Normalizes_Separators()
    {
        LocalImportPathRules.TryNormalizeRelativePath("videos\\clip.mp4", out var normalized, out var error)
            .ShouldBeTrue(error);

        normalized.ShouldBe("videos/clip.mp4");
    }

    [Arguments("/etc/passwd")]
    [Arguments("../clip.mp4")]
    [Arguments("videos/../clip.mp4")]
    [Arguments("C:\\media\\clip.mp4")]
    [Test]
    public void TryNormalizeRelativePath_Rejects_Unsafe_Paths(string path)
    {
        LocalImportPathRules.TryNormalizeRelativePath(path, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryResolveUnderAllowedRoots_Allows_Path_Inside_Configured_Root()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), "froststream-import-tests");
        var sourceRoot = Path.Combine(allowedRoot, "batch-a");

        LocalImportPathRules.TryResolveUnderAllowedRoots(
                sourceRoot,
                "nested/clip.mp4",
                [allowedRoot],
                out var fullPath,
                out var normalized,
                out var error)
            .ShouldBeTrue(error);

        normalized.ShouldBe("nested/clip.mp4");
        fullPath.ShouldBe(Path.GetFullPath(Path.Combine(sourceRoot, "nested", "clip.mp4")));
    }

    [Test]
    public void TryResolveUnderAllowedRoots_Rejects_Path_Outside_Configured_Root()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "froststream-import-source");
        var allowedRoot = Path.Combine(Path.GetTempPath(), "froststream-import-allowed");

        LocalImportPathRules.TryResolveUnderAllowedRoots(
                sourceRoot,
                "clip.mp4",
                [allowedRoot],
                out _,
                out _,
                out var error)
            .ShouldBeFalse();

        error.ShouldContain("allowed import roots");
    }

    [Test]
    public void TryResolveUnderAllowedRoots_Rejects_Relative_SourceRoot()
    {
        LocalImportPathRules.TryResolveUnderAllowedRoots(
                "relative-root",
                "clip.mp4",
                [Path.GetTempPath()],
                out _,
                out _,
                out var error)
            .ShouldBeFalse();

        error.ShouldContain("sourceRoot");
    }
}
