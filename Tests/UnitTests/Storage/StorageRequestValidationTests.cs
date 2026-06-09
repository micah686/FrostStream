using Shouldly;
using TUnit.Core;
using WebAPI.Features.Storage.Models;

namespace UnitTests.Storage;

public class StorageRequestValidationTests
{
    [Test]
    public void Local_Upsert_Request_Rejects_Bad_Key()
    {
        var results = StorageTestHelpers.ValidateObject(new LocalStorageUpsertRequest
        {
            Key = "Bad_Key",
            Protocol = Shared.Storage.LocalStorageProtocol.Local,
            Path = "/tmp"
        });

        results.Select(x => x.ErrorMessage).ShouldContain("The field Key must match the regular expression '^[a-z0-9-]{2,100}$'.");
    }

    [Test]
    public void Local_Update_Request_Requires_Path()
    {
        var results = StorageTestHelpers.ValidateObject(new LocalStorageUpdateRequest
        {
            Protocol = Shared.Storage.LocalStorageProtocol.Local,
            Path = null!
        });

        results.Select(x => x.ErrorMessage).ShouldContain("The Path field is required.");
    }

    [Test]
    public void Network_Upsert_Request_Rejects_Port_Range()
    {
        var results = StorageTestHelpers.ValidateObject(new NetworkStorageUpsertRequest
        {
            Key = "network-a",
            Protocol = Shared.Storage.NetworkStorageProtocol.Sftp,
            Host = "host",
            Port = 70000
        });

        results.Select(x => x.ErrorMessage).ShouldContain("The field Port must be between 1 and 65535.");
    }

    [Test]
    public void Network_Upsert_Request_Rejects_Conflicting_Auth()
    {
        var results = StorageTestHelpers.ValidateObject(new NetworkStorageUpsertRequest
        {
            Key = "network-a",
            Protocol = Shared.Storage.NetworkStorageProtocol.Sftp,
            Host = "host",
            Username = "micah",
            Password = "pw",
            PrivateKey = "key"
        });

        results.Select(x => x.ErrorMessage).ShouldContain("Use either password-based auth or privateKey auth, not both.");
    }

    [Test]
    public void Network_Update_Request_Requires_Username_When_Password_Set()
    {
        var results = StorageTestHelpers.ValidateObject(new NetworkStorageUpdateRequest
        {
            Protocol = Shared.Storage.NetworkStorageProtocol.Sftp,
            Host = "host",
            Password = "pw"
        });

        results.Select(x => x.ErrorMessage).ShouldContain("Username is required when password or privateKey is provided.");
    }

    [Test]
    public void S3_Upsert_Request_Requires_Region_For_Aws()
    {
        var results = StorageTestHelpers.ValidateObject(new S3CompatibleObjectStorageUpsertRequest
        {
            Key = "s3-a",
            Provider = Shared.Storage.S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });

        results.Select(x => x.ErrorMessage).ShouldContain("region is required for AwsS3 and DigitalOceanSpaces.");
    }

    [Test]
    public void S3_Update_Request_Requires_Endpoint_For_MinIo()
    {
        var results = StorageTestHelpers.ValidateObject(new S3CompatibleObjectStorageUpdateRequest
        {
            Provider = Shared.Storage.S3CompatibleObjectStorageProvider.MinIo,
            BucketName = "bucket",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });

        results.Select(x => x.ErrorMessage).ShouldContain("endpoint is required for MinIo.");
    }

    [Test]
    public void Azure_Upsert_Request_Requires_Mode_Specific_Fields()
    {
        var results = StorageTestHelpers.ValidateObject(new AzureBlobObjectStorageUpsertRequest
        {
            Key = "azure-a",
            CredentialMode = Shared.Storage.AzureBlobCredentialMode.AccountKey
        });

        results.Select(x => x.ErrorMessage).ShouldContain("azureAccountName is required when credentialMode is AccountKey.");
        results.Select(x => x.ErrorMessage).ShouldContain("azureAccountKeySecretId is required when credentialMode is AccountKey.");
    }

    [Test]
    public void Azure_Update_Request_Requires_Sas_SecretId()
    {
        var results = StorageTestHelpers.ValidateObject(new AzureBlobObjectStorageUpdateRequest
        {
            CredentialMode = Shared.Storage.AzureBlobCredentialMode.SasUrl
        });

        results.Select(x => x.ErrorMessage).ShouldContain("azureSasUrlSecretId is required when credentialMode is SasUrl.");
    }

    [Test]
    public void Gcs_Upsert_Request_Requires_Json_For_CredentialsJson_Mode()
    {
        var results = StorageTestHelpers.ValidateObject(new GoogleCloudStorageObjectStorageUpsertRequest
        {
            Key = "gcs-a",
            BucketName = "bucket",
            CredentialMode = Shared.Storage.GoogleCloudStorageCredentialMode.CredentialsJson
        });

        results.Select(x => x.ErrorMessage).ShouldContain("gcpCredentialsJson is required when credentialMode is CredentialsJson.");
    }

    [Test]
    public void Gcs_Update_Request_Requires_FilePath_For_File_Mode()
    {
        var results = StorageTestHelpers.ValidateObject(new GoogleCloudStorageObjectStorageUpdateRequest
        {
            BucketName = "bucket",
            CredentialMode = Shared.Storage.GoogleCloudStorageCredentialMode.CredentialsFilePath
        });

        results.Select(x => x.ErrorMessage).ShouldContain("gcpCredentialsFilePath is required when credentialMode is CredentialsFilePath.");
    }
}
