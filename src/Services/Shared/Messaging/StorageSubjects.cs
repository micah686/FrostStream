namespace Shared.Messaging;

public static class StorageSubjects
{
    public const string CreateLocalStorage = "storage.local.create";
    public const string CreateStreamingStorage = "storage.streaming.create";
    public const string CreateObjectStorage = "storage.object.create";
    public const string UpdateLocalStorage = "storage.local.update";
    public const string UpdateStreamingStorage = "storage.streaming.update";
    public const string UpdateObjectStorage = "storage.object.update";
    public const string ListStorage = "storage.list";
    public const string GetStorage = "storage.get";
    public const string DeleteStorage = "storage.delete";
}
