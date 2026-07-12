using Shouldly;
using TUnit.Core;
using WebAPI.Features.Common;

namespace UnitTests.WebAPI;

public sealed class PlaylistUrlDetectorTests
{
    [Test]
    [Arguments("https://www.youtube.com/playlist?list=PL123")]
    [Arguments("https://youtube.com/playlist?list=PL123")]
    [Arguments("https://m.youtube.com/playlist?list=PL123")]
    [Arguments("https://music.youtube.com/playlist?list=OLAK5uy_123")]
    [Arguments("https://www.youtube.com/watch?list=PL123")]
    public void IsPlaylistUrl_Matches_Unambiguous_Playlist_Containers(string url)
        => PlaylistUrlDetector.IsPlaylistUrl(url).ShouldBeTrue();

    [Test]
    [Arguments("https://www.youtube.com/watch?v=abc&list=PL123")] // specific video in a playlist
    [Arguments("https://youtu.be/abc?list=PL123")] // youtu.be always references a video
    [Arguments("https://www.youtube.com/watch?v=abc")]
    [Arguments("https://www.youtube.com/playlist")] // no list param
    [Arguments("https://www.youtube.com/@Name/playlists")]
    [Arguments("https://vimeo.com/album/123")] // non-YouTube stays direct
    [Arguments("not a url")]
    public void IsPlaylistUrl_Stays_Direct_For_Ambiguous_Or_Non_Playlist_Urls(string url)
        => PlaylistUrlDetector.IsPlaylistUrl(url).ShouldBeFalse();
}
