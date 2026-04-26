namespace Shared.Messaging;

public static class StorageSubjects
{
    public const string CreateLocalStorage = "storage.local.create";
    public const string CreateNetworkStorage = "storage.network.create";
    public const string CreateS3CompatibleObjectStorage = "storage.object.s3-compatible.create";
    public const string CreateAzureBlobObjectStorage = "storage.object.azure-blob.create";
    public const string CreateGoogleCloudStorageObjectStorage = "storage.object.google-cloud-storage.create";
    public const string UpdateLocalStorage = "storage.local.update";
    public const string UpdateNetworkStorage = "storage.network.update";
    public const string UpdateS3CompatibleObjectStorage = "storage.object.s3-compatible.update";
    public const string UpdateAzureBlobObjectStorage = "storage.object.azure-blob.update";
    public const string UpdateGoogleCloudStorageObjectStorage = "storage.object.google-cloud-storage.update";
    public const string ListStorage = "storage.list";
    public const string GetStorage = "storage.get";
    public const string DeleteStorage = "storage.delete";
}
