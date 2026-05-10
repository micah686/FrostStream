using Typesense;

namespace DataBridge.Search;

public static class MediaCollectionSchema
{
    public const string CollectionName = "media";

    public static Schema Build()
        => new(
            CollectionName,
            [
                new Field("id", FieldType.String),
                new Field("title", FieldType.String) { Infix = true },
                new Field("description", FieldType.String) { Optional = true },
                new Field("account_name", FieldType.String),
                new Field("account_handle", FieldType.String) { Facet = true },
                new Field("tags", FieldType.StringArray) { Facet = true, Optional = true },
                new Field("categories", FieldType.StringArray) { Facet = true, Optional = true },
                new Field("genres", FieldType.StringArray) { Facet = true, Optional = true },
                new Field("artists", FieldType.StringArray) { Facet = true, Optional = true },
                new Field("caption_languages", FieldType.StringArray) { Facet = true, Optional = true },
                new Field("platform", FieldType.String) { Facet = true },
                new Field("availability", FieldType.String) { Optional = true },
                new Field("was_live", FieldType.Bool),
                new Field("age_limit", FieldType.Int32) { Optional = true },
                new Field("account_id", FieldType.Int64) { Facet = true },
                new Field("release_date_unix", FieldType.Int64) { Sort = true, Optional = true },
                new Field("release_date_sort", FieldType.Int64) { Sort = true },
                new Field("view_count", FieldType.Int64) { Sort = true, Optional = true },
                new Field("like_count", FieldType.Int64) { Sort = true, Optional = true },
                new Field("duration_seconds", FieldType.Float) { Sort = true, Optional = true },
                new Field("thumbnail_storage_path", FieldType.String) { Index = false, Optional = true },
                new Field("account_avatar_storage_path", FieldType.String) { Index = false, Optional = true },
                new Field("webpage_url", FieldType.String) { Index = false, Optional = true }
            ],
            "release_date_sort");
}
