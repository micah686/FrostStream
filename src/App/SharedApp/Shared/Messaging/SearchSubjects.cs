namespace Shared.Messaging;

public static class SearchSubjects
{
    /// <summary>Advanced unified search across metadata, subtitles, and comments.</summary>
    public const string Query = "search.query";

    /// <summary>Content-based "find similar media" for a given media item.</summary>
    public const string Similar = "search.similar";

    // Reuse DataBridge's existing search queue group so unified search load-balances
    // across replicas alongside the other metadata read consumers.
    public const string QueueGroup = MetadataSubjects.SearchQueueGroup;
}

/// <summary>Where a unified-search hit matched. Surfaced on <see cref="SearchHitDto.MatchedIn"/>.</summary>
public static class SearchMatch
{
    public const string Metadata = "metadata";
    public const string Subtitles = "subtitles";
    public const string Comments = "comments";
    public const string Notes = "notes";
    public const string Similar = "similar";
}

/// <summary>Which content surfaces a unified-search query should span.</summary>
public static class SearchScope
{
    public const string All = "all";
    public const string Metadata = "metadata";
    public const string Subtitles = "subtitles";
    public const string Comments = "comments";
}
