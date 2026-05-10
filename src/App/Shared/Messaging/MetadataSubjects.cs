namespace Shared.Messaging;

public static class MetadataSubjects
{
    public const string List = "metadata.list";
    public const string Get = "metadata.get";
    public const string GetTechnical = "metadata.get-technical";
    public const string CommentsList = "metadata.comments.list";
    public const string CaptionsList = "metadata.captions.list";
    public const string Search = "metadata.search";
    public const string AccountsList = "metadata.accounts.list";
    public const string AccountsGet = "metadata.accounts.get";
    public const string AccountsMediaList = "metadata.accounts.media.list";
    public const string TaxonomyTagsList = "metadata.taxonomy.tags.list";
    public const string TaxonomyCategoriesList = "metadata.taxonomy.categories.list";
    public const string TaxonomyGenresList = "metadata.taxonomy.genres.list";

    public const string SearchQueueGroup = "databridge-search";
    public const string ProcessorsQueueGroup = "databridge-processors";
}
