using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageParametersSerializerTests
{
    [Test]
    public void Serialize_And_Deserialize_Local_RoundTrips()
    {
        var parameters = new PosixLocalStorageParameters
        {
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/storage"
        };

        var json = StorageParametersSerializer.Serialize(StorageMethod.Local, parameters);
        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.Local, json, out var parsed, out var error);

        ok.ShouldBeTrue(error);
        parsed.ShouldBeOfType<PosixLocalStorageParameters>();
        StorageTestHelpers.Serialize(parsed).ShouldBe(StorageTestHelpers.Serialize(parameters));
    }

    [Test]
    public void Serialize_And_Deserialize_Network_RoundTrips()
    {
        var parameters = new StreamingNetworkStorageParameters
        {
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "example.test",
            Port = 2222,
            Username = "micah",
            Password = "pw",
            BasePath = "/drop"
        };

        var json = StorageParametersSerializer.Serialize(StorageMethod.Network, parameters);
        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.Network, json, out var parsed, out var error);

        ok.ShouldBeTrue(error);
        StorageTestHelpers.Serialize(parsed!).ShouldBe(StorageTestHelpers.Serialize(parameters));
    }

    [Test]
    public void Serialize_And_Deserialize_S3_RoundTrips()
    {
        var parameters = new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.MinIo,
            BucketName = "bucket",
            Region = "us-east-1",
            Endpoint = "https://minio.example.test",
            AccessKeyId = "access",
            SecretKeyId = "secret",
            SessionTokenSecretId = "session",
            ForcePathStyle = true,
            UseSsl = false
        };

        var json = StorageParametersSerializer.Serialize(StorageMethod.ObjectStorage, parameters);
        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.ObjectStorage, json, out var parsed, out var error);

        ok.ShouldBeTrue(error);
        parsed.ShouldBeOfType<S3CompatibleObjectStorageParameters>();
        StorageTestHelpers.Serialize(parsed).ShouldBe(StorageTestHelpers.Serialize(parameters));
    }

    [Test]
    public void Serialize_And_Deserialize_Azure_RoundTrips()
    {
        var parameters = new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.AccountKey,
            ContainerName = "container",
            AzureAccountName = "account",
            AzureAccountKeySecretId = "secret"
        };

        var json = StorageParametersSerializer.Serialize(StorageMethod.ObjectStorage, parameters);
        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.ObjectStorage, json, out var parsed, out var error);

        ok.ShouldBeTrue(error);
        parsed.ShouldBeOfType<AzureBlobObjectStorageParameters>();
        StorageTestHelpers.Serialize(parsed).ShouldBe(StorageTestHelpers.Serialize(parameters));
    }

    [Test]
    public void Serialize_And_Deserialize_Gcs_RoundTrips()
    {
        var parameters = new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson,
            GcpCredentialsJson = StorageTestHelpers.Json("""{"client_email":"svc@example.test"}"""),
            GcpCredentialsJsonIsBase64Encoded = false,
            GcpProjectId = "proj"
        };

        var json = StorageParametersSerializer.Serialize(StorageMethod.ObjectStorage, parameters);
        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.ObjectStorage, json, out var parsed, out var error);

        ok.ShouldBeTrue(error);
        parsed.ShouldBeOfType<GoogleCloudStorageObjectStorageParameters>();
        StorageTestHelpers.Serialize(parsed).ShouldBe(StorageTestHelpers.Serialize(parameters));
    }

    [Test]
    public void Deserialize_ObjectStorage_Uses_Discriminator_For_S3()
    {
        var json = StorageParametersSerializer.Serialize(StorageMethod.ObjectStorage, new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            Region = "us-west-2",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });

        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.ObjectStorage, json, out var parsed, out _);

        ok.ShouldBeTrue();
        parsed.ShouldBeOfType<S3CompatibleObjectStorageParameters>();
    }

    [Test]
    public void Deserialize_ObjectStorage_Uses_Discriminator_For_Azure()
    {
        var json = StorageParametersSerializer.Serialize(StorageMethod.ObjectStorage, new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.SasUrl,
            ContainerName = "container",
            AzureSasUrlSecretId = "sas"
        });

        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.ObjectStorage, json, out var parsed, out _);

        ok.ShouldBeTrue();
        parsed.ShouldBeOfType<AzureBlobObjectStorageParameters>();
    }

    [Test]
    public void Deserialize_ObjectStorage_Uses_Discriminator_For_Gcs()
    {
        var json = StorageParametersSerializer.Serialize(StorageMethod.ObjectStorage, new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsFilePath,
            GcpCredentialsFilePath = "/tmp/creds.json"
        });

        var ok = StorageParametersSerializer.TryDeserialize(StorageMethod.ObjectStorage, json, out var parsed, out _);

        ok.ShouldBeTrue();
        parsed.ShouldBeOfType<GoogleCloudStorageObjectStorageParameters>();
    }

    [Test]
    public void Validate_S3_Requires_Region_For_AwsS3_And_DigitalOceanSpaces()
    {
        var awsErrors = StorageParametersSerializer.Validate(new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });
        var doErrors = StorageParametersSerializer.Validate(new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.DigitalOceanSpaces,
            BucketName = "bucket",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });

        awsErrors.ShouldContain("region is required for AwsS3 and DigitalOceanSpaces.");
        doErrors.ShouldContain("region is required for AwsS3 and DigitalOceanSpaces.");
    }

    [Test]
    public void Validate_S3_MinIo_Requires_Endpoint()
    {
        var errors = StorageParametersSerializer.Validate(new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.MinIo,
            BucketName = "bucket",
            Region = "us-east-1",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });

        errors.ShouldContain("endpoint is required for MinIo.");
    }

    [Test]
    public void Validate_Azure_Requires_Mode_Specific_Fields()
    {
        StorageParametersSerializer.Validate(new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.AccountKey
        }).ShouldContain("azureAccountName is required when credentialMode is AccountKey.");

        StorageParametersSerializer.Validate(new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.ConnectionString
        }).ShouldContain("azureConnectionStringSecretId is required when credentialMode is ConnectionString.");

        StorageParametersSerializer.Validate(new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.SasUrl
        }).ShouldContain("azureSasUrlSecretId is required when credentialMode is SasUrl.");
    }

    [Test]
    public void Validate_Gcs_Requires_Mode_Specific_Fields()
    {
        StorageParametersSerializer.Validate(new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson
        }).ShouldContain("gcpCredentialsJson is required when credentialMode is CredentialsJson.");

        StorageParametersSerializer.Validate(new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsFilePath
        }).ShouldContain("gcpCredentialsFilePath is required when credentialMode is CredentialsFilePath.");
    }

    [Test]
    public void Validate_Network_Rejects_Password_And_PrivateKey_Together()
    {
        var errors = StorageParametersSerializer.Validate(new StreamingNetworkStorageParameters
        {
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "example.test",
            Username = "micah",
            Password = "pw",
            PrivateKey = "key"
        });

        errors.ShouldContain("Use either password-based auth or privateKey auth, not both.");
    }
}
