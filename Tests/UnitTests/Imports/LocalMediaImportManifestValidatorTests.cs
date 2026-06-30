using Shared.Imports;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Imports;

public sealed class LocalMediaImportManifestValidatorTests
{
    [Test]
    public void Validate_Accepts_Minimal_Manifest()
    {
        var result = LocalMediaImportManifestValidator.Validate(new LocalMediaImportManifest
        {
            Items =
            [
                new LocalMediaImportManifestItem { File = "video.mp4" }
            ]
        });

        result.IsValid.ShouldBeTrue(string.Join(" ", result.Errors));
    }

    [Test]
    public void Validate_Rejects_Empty_Items()
    {
        var result = LocalMediaImportManifestValidator.Validate(new LocalMediaImportManifest());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain("Manifest must contain at least one item.");
    }

    [Test]
    public void Validate_Rejects_Traversal_In_Item_And_Sidecar_Paths()
    {
        var result = LocalMediaImportManifestValidator.Validate(new LocalMediaImportManifest
        {
            Items =
            [
                new LocalMediaImportManifestItem
                {
                    File = "../video.mp4",
                    Sidecars = new LocalMediaImportManifestSidecars
                    {
                        InfoJson = "meta/../video.info.json",
                        Thumbnail = "/tmp/thumb.jpg",
                        Captions =
                        [
                            new LocalMediaImportCaptionSidecar { File = "../caption.vtt" }
                        ]
                    }
                }
            ]
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(4);
    }
}
