namespace Shared.Messaging;

public static class PlaylistSubjects
{
    public const string PlaylistRequested                  = "playlist.requested";

    public const string FetchPlaylistMetadataCommand       = "playlist.cmd.fetch-metadata";
    public const string ProcessPlaylistStagedEntriesCommand = "playlist.cmd.process-staged-entries";

    public const string PlaylistMetadataFetched            = "playlist.evt.metadata-fetched";
    public const string PlaylistMetadataFetchFailed        = "playlist.evt.metadata-fetch-failed";

    public const string PlaylistGet                        = "playlist.get";
    public const string PlaylistList                       = "playlist.list";
    public const string PlaylistItemForceQueue             = "playlist.item.force-queue";

    public const string UserPlaylistCreate                 = "playlist.user.create";
    public const string UserPlaylistUpdate                 = "playlist.user.update";
    public const string UserPlaylistDelete                 = "playlist.user.delete";
    public const string UserPlaylistGet                    = "playlist.user.get";
    public const string UserPlaylistList                   = "playlist.user.list";
    public const string UserPlaylistAddItem                = "playlist.user.item.add";
    public const string UserPlaylistRemoveItem             = "playlist.user.item.remove";
    public const string UserPlaylistReorderItems           = "playlist.user.item.reorder";
}
