using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class UserPlaylistsRepositoryTests
{
    [Test]
    public async Task Create_And_List_Are_Owner_Scoped()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new UserPlaylistsRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var micah = await repo.CreateAsync("micah", "Watch later", "queue");
        await repo.CreateAsync("other", "Other list", null);

        var mine = await repo.ListAsync("micah", pageSize: 50, pageOffset: 0);
        var other = await repo.ListAsync("other", pageSize: 50, pageOffset: 0);

        mine.Single().Playlist.PlaylistId.ShouldBe(micah.Playlist.PlaylistId);
        mine.Single().Playlist.Name.ShouldBe("Watch later");
        mine.Single().ItemCount.ShouldBe(0);
        other.Single().Playlist.OwnerSubject.ShouldBe("other");
    }

    [Test]
    public async Task Get_And_Update_Require_Owner()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new UserPlaylistsRepository(db, new FixedClock(DataBridgeTestHelpers.Now));
        var created = await repo.CreateAsync("micah", "Original", null);

        (await repo.GetAsync("other", created.Playlist.PlaylistId)).ShouldBeNull();
        (await repo.UpdateAsync("other", created.Playlist.PlaylistId, "Nope", null)).ShouldBeNull();

        var updated = await repo.UpdateAsync("micah", created.Playlist.PlaylistId, "Updated", "description");

        updated.ShouldNotBeNull();
        updated.Playlist.Name.ShouldBe("Updated");
        updated.Playlist.Description.ShouldBe("description");
        (await db.UserPlaylists.SingleAsync()).OwnerSubject.ShouldBe("micah");
    }
}
