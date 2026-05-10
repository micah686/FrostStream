using Typesense;

namespace DataBridge.Search;

public static class CaptionsCollectionSchema
{
    public const string CollectionName = "captions";

    public static Schema Build()
        => new(
            CollectionName,
            [
                new Field("id", FieldType.String),
                new Field("media_guid", FieldType.String) { Facet = true },
                new Field("language_code", FieldType.String) { Facet = true },
                new Field("caption_type", FieldType.String) { Facet = true },
                new Field("name", FieldType.String) { Optional = true },
                new Field("storage_path", FieldType.String) { Index = false }
            ]);
}
