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
}
