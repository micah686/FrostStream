using Typesense;

namespace DataBridge.Search;

public static class CommentsCollectionSchema
{
    public const string CollectionName = "comments";

    public static Schema Build()
        => new(
            CollectionName,
            [
                new Field("id", FieldType.String),
                new Field("media_guid", FieldType.String) { Facet = true },
                new Field("parent_comment_id", FieldType.String) { Facet = true },
                new Field("text", FieldType.String),
                new Field("comment_timestamp_unix", FieldType.Int64) { Sort = true },
                new Field("like_count", FieldType.Int32) { Sort = true, Optional = true },
                new Field("dislike_count", FieldType.Int32) { Optional = true },
                new Field("is_favorited", FieldType.Bool),
                new Field("is_pinned", FieldType.Bool),
                new Field("is_uploader", FieldType.Bool),
                new Field("account_id", FieldType.Int64),
                new Field("account_name", FieldType.String),
                new Field("account_handle", FieldType.String),
                new Field("platform", FieldType.String) { Facet = true },
                new Field("account_avatar_storage_path", FieldType.String) { Index = false, Optional = true }
            ],
            "comment_timestamp_unix");
}
