using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class OptionPresetsRepositoryTests
{
    [Test]
    public async Task Create_Update_List_And_Delete_RoundTrip()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new OptionPresetsRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var created = await repo.CreateAsync("video-default", "Video Default", "desc", """{"Verbose":false}""");
        await repo.CreateAsync("audio-high", "Audio High", null, """{"Verbose":true}""");

        created.Key.ShouldBe("video-default");
        created.LastUpdated.ShouldBeNull();

        var listed = await repo.ListAsync();
        listed.Select(x => x.Key).ShouldBe(["audio-high", "video-default"]);

        var updated = await repo.UpdateAsync("video-default", "Video Updated", null, """{"NoWarnings":true}""");
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("Video Updated");
        updated.Description.ShouldBeNull();
        updated.YtDlpOptionsJson.ShouldBe("""{"NoWarnings":true}""");
        updated.LastUpdated.ShouldBe(DataBridgeTestHelpers.Now);

        var fetched = await repo.GetByKeyAsync("video-default");
        fetched.ShouldNotBeNull();
        fetched.Name.ShouldBe("Video Updated");

        (await repo.DeleteAsync("audio-high")).ShouldBeTrue();
        (await repo.DeleteAsync("missing")).ShouldBeFalse();
        (await db.OptionPresets.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task Update_Returns_Null_When_Preset_Is_Missing()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new OptionPresetsRepository(db, SystemClock.Instance);

        var updated = await repo.UpdateAsync("missing", "Missing", null, "{}");

        updated.ShouldBeNull();
        (await db.OptionPresets.CountAsync()).ShouldBe(0);
    }
}
