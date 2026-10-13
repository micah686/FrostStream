using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Database;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class UserNotesRepositoryTests
{
    [Test]
    public async Task Upsert_Get_Search_And_Delete_Are_Owner_Scoped()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var mediaGuid = Guid.NewGuid();
        db.Media.Add(new MediaEntity { MediaGuid = mediaGuid });
        await db.SaveChangesAsync();

        var repo = new UserNotesRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var created = await repo.UpsertAsync("micah", "video", mediaGuid.ToString(), "  Remember the bridge scene  ");
        await repo.UpsertAsync("other", "video", mediaGuid.ToString(), "Other user's bridge note");

        created.Success.ShouldBeTrue();
        created.Note!.TargetId.ShouldBe(mediaGuid.ToString("N"));
        created.Note.Note.ShouldBe("Remember the bridge scene");

        var mine = await repo.GetAsync("micah", "videos", mediaGuid.ToString("N"));
        var other = await repo.GetAsync("other", "video", mediaGuid.ToString("N"));
        mine.ShouldNotBeNull();
        mine.Note.ShouldBe("Remember the bridge scene");
        other.ShouldNotBeNull();
        other.Note.ShouldBe("Other user's bridge note");

        var search = await repo.SearchAsync("micah", "bridge", targetType: null, pageSize: 10, pageOffset: 0);
        search.TotalCount.ShouldBe(1);
        search.Items.Single().OwnerSubject.ShouldBe("micah");

        (await repo.DeleteAsync("micah", "video", mediaGuid.ToString())).ShouldBeTrue();
        (await repo.GetAsync("micah", "video", mediaGuid.ToString())).ShouldBeNull();
        (await repo.GetAsync("other", "video", mediaGuid.ToString())).ShouldNotBeNull();
    }

    [Test]
    public async Task Upsert_Rejects_Missing_Target()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new UserNotesRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var result = await repo.UpsertAsync("micah", "video", Guid.NewGuid().ToString(), "note");

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("target_not_found");
        (await db.UserNotes.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Playlist_Target_Can_Reference_User_Playlist_Owned_By_User()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var playlistId = Guid.NewGuid();
        db.UserPlaylists.Add(new UserPlaylistEntity
        {
            PlaylistId = playlistId,
            OwnerSubject = "micah",
            Name = "Research"
        });
        await db.SaveChangesAsync();

        var repo = new UserNotesRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var mine = await repo.UpsertAsync("micah", "playlist", playlistId.ToString(), "playlist note");
        var other = await repo.UpsertAsync("other", "playlist", playlistId.ToString(), "nope");

        mine.Success.ShouldBeTrue();
        other.Success.ShouldBeFalse();
        other.ErrorCode.ShouldBe("target_not_found");
    }
}
