using NodaTime;
using Shared.Media;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Media;

public class MediaSourceIdentityTests
{
    [Test]
    public void TryCreateSourceMetadataHash_Is_Deterministic_And_Normalizes_Provider()
    {
        var lastModified = Instant.FromUtc(2026, 5, 2, 12, 30);

        var first = MediaSourceIdentity.TryCreateSourceMetadataHash(" YouTube ", "abc123", lastModified);
        var second = MediaSourceIdentity.TryCreateSourceMetadataHash("youtube", "abc123", lastModified);

        first.ShouldNotBeNull();
        first.Length.ShouldBe(32);
        second.ShouldBe(first);
    }

    [Test]
    public void TryCreateSourceMetadataHash_Changes_When_SourceLastModified_Changes()
    {
        var first = MediaSourceIdentity.TryCreateSourceMetadataHash(
            "youtube",
            "abc123",
            Instant.FromUtc(2026, 5, 2, 12, 30));
        var second = MediaSourceIdentity.TryCreateSourceMetadataHash(
            "youtube",
            "abc123",
            Instant.FromUtc(2026, 5, 2, 12, 31));

        second.ShouldNotBe(first);
    }

    [Test]
    public void TryCreateSourceMetadataHash_Returns_Null_When_Metadata_Cannot_Safely_Dedupe()
    {
        MediaSourceIdentity.TryCreateSourceMetadataHash("generic", null, null).ShouldBeNull();
        MediaSourceIdentity.TryCreateSourceMetadataHash(null, "abc123", null).ShouldBeNull();
    }
}
