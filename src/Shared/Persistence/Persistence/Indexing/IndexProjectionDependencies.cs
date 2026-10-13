namespace DataBridge.Persistence.Indexing;

/// <summary>Source relationships used by the existing media/comment/caption projections.</summary>
public static class IndexProjectionDependencies
{
    public sealed record Dependency(string Table,string Select);
    public static IReadOnlyList<Dependency> All { get; } = Build();
    private static IReadOnlyList<Dependency> Build()
    {
        var result=new List<Dependency>();
        foreach(var (association,lookup,key) in new[]{("media_tags","tags","tag_id"),("media_categories","categories","category_id"),("media_genres","genres","genre_id"),("media_artists","artists","artist_id")}) {
            result.Add(new("metadata."+association,"SELECT m.media_guid FROM {metadata.media_metadata} m WHERE m.id={row}.media_metadata_id"));
            result.Add(new("metadata."+lookup,"SELECT m.media_guid FROM {metadata.media_metadata} m JOIN {metadata."+association+"} a ON a.media_metadata_id=m.id WHERE a."+key+"={row}.id"));
        }
        result.Add(new("metadata.accounts","SELECT media_guid FROM {metadata.media_metadata} WHERE account_id={row}.id UNION SELECT media_guid FROM {metadata.media_comments} WHERE account_id={row}.id"));
        result.Add(new("metadata.media_streams","SELECT media_guid FROM {metadata.media_base} WHERE id={row}.media_base_id"));
        foreach(var detail in new[]{"video_stream_details","audio_stream_details"})
            result.Add(new("metadata."+detail,"SELECT b.media_guid FROM {metadata.media_base} b JOIN {metadata.media_streams} s ON s.media_base_id=b.id WHERE s.id={row}.media_stream_id"));
        return result;
    }
    public static string Render(Dependency dependency,PersistenceProvider provider,string row)
        => System.Text.RegularExpressions.Regex.Replace(dependency.Select,@"\{([^}]+)\}",match=>match.Groups[1].Value=="row"?row:ApplicationQueries.Table(provider,match.Groups[1].Value));
}
