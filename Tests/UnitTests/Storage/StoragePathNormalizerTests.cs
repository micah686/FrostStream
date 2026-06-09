using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class StoragePathNormalizerTests
{
    [Test]
    public void Normalize_Uses_Filesystem_Rescan_Canonical_Path_Rule()
    {
        (string? Input, string Expected)[] cases =
        [
            (null, "/"),
            ("", "/"),
            ("///", "/"),
            ("media/file.mp4", "/media/file.mp4"),
            ("/media/file.mp4", "/media/file.mp4"),
            ("//media///file.mp4//", "/media/file.mp4"),
            (@"media\folder\file.mp4", "/media/folder/file.mp4"),
            (@"/media\\folder//file.mp4/", "/media/folder/file.mp4"),
            ("  media file.mp4  ", "/  media file.mp4  "),
            ("Ünicode//路径/file.mp4", "/Ünicode/路径/file.mp4")
        ];

        foreach (var (input, expected) in cases)
        {
            StoragePathNormalizer.Normalize(input).ShouldBe(expected);
        }
    }
}
