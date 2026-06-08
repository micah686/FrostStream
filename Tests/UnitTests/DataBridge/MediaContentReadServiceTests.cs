using DataBridge.Data;
using DataBridge.MediaContent;
using Microsoft.EntityFrameworkCore;
using Shared.Database;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class MediaContentReadServiceTests
{
    [Test]
    public async Task ResolveAsync_Selects_Highest_Matching_Version()
    {
        await using var db = CreateDbContext();
        var mediaGuid = Guid.NewGuid();
        db.MediaContentIdVersions.AddRange(
            Content(mediaGuid, "storage-a", "media/v1.mp4", 1),
            Content(mediaGuid, "storage-b", "media/v3.mp4", 3),
            Content(mediaGuid, "storage-a", "media/v2.mp4", 2));
        await db.SaveChangesAsync();

        var service = new MediaContentReadService(db);

        var latest = await service.ResolveAsync(mediaGuid, null, null);
        latest.ShouldNotBeNull();
        latest.Version.ShouldBe(3);
        latest.StorageKey.ShouldBe("storage-b");

        var latestInStorage = await service.ResolveAsync(mediaGuid, "storage-a", null);
        latestInStorage.ShouldNotBeNull();
        latestInStorage.Version.ShouldBe(2);

        var explicitVersion = await service.ResolveAsync(mediaGuid, null, 1);
        explicitVersion.ShouldNotBeNull();
        explicitVersion.StoragePath.ShouldBe("media/v1.mp4");
    }

    [Test]
    public async Task ResolveAsync_Returns_Null_When_Filters_Do_Not_Match()
    {
        await using var db = CreateDbContext();
        var mediaGuid = Guid.NewGuid();
        db.MediaContentIdVersions.Add(Content(mediaGuid, "storage-a", "media/v1.mp4", 1));
        await db.SaveChangesAsync();

        var service = new MediaContentReadService(db);

        (await service.ResolveAsync(mediaGuid, "storage-b", null)).ShouldBeNull();
        (await service.ResolveAsync(mediaGuid, "storage-a", 2)).ShouldBeNull();
        (await service.ResolveAsync(Guid.NewGuid(), null, null)).ShouldBeNull();
    }

    private static DataBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DataBridgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        return new DataBridgeDbContext(options);
    }

    private static MediaContentIdVersionEntity Content(
        Guid mediaGuid,
        string storageKey,
        string storagePath,
        int version)
        => new()
        {
            MediaGuid = mediaGuid,
            ContentHashXxh128 = $"{storageKey}-{version}",
            StorageKey = storageKey,
            StoragePath = storagePath,
            VersionNum = version
        };
}
