using System.Text.Json;
using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class StorageSecretSplitterTests
{
    [Test]
    public void Split_Moves_Only_Secrets_And_Hydrate_RoundTrips_All_Storage_Types()
    {
        var cases = new Dictionary<StorageParametersBase, IReadOnlyCollection<string>>
        {
            [new PosixLocalStorageParameters
            {
                Protocol = LocalStorageProtocol.Local,
                Path = "/data"
            }] = [],
            [new StreamingNetworkStorageParameters
            {
                Protocol = NetworkStorageProtocol.Sftp,
                Host = "example.test",
                Port = 22,
                Username = "micah",
                Password = "pw",
                PrivateKey = null,
                PublicKey = "pub",
                BasePath = "/upload"
            }] = [StorageSecretSplitter.NetworkPassword, StorageSecretSplitter.NetworkPublicKey],
            [new S3CompatibleObjectStorageParameters
            {
                Provider = S3CompatibleObjectStorageProvider.AwsS3,
                BucketName = "bucket",
                Region = "us-west-2",
                AccessKeyId = "access",
                SecretKeyId = "secret",
                SessionTokenSecretId = "session",
                ForcePathStyle = true,
                UseSsl = false
            }] = [StorageSecretSplitter.S3AccessKeyId, StorageSecretSplitter.S3SecretKeyId, StorageSecretSplitter.S3SessionToken],
            [new AzureBlobObjectStorageParameters
            {
                CredentialMode = AzureBlobCredentialMode.ConnectionString,
                ContainerName = "container",
                AzureConnectionStringSecretId = "UseDevelopmentStorage=true"
            }] = [StorageSecretSplitter.AzureConnectionString],
            [new GoogleCloudStorageObjectStorageParameters
            {
                BucketName = "bucket",
                CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson,
                GcpCredentialsJson = StorageTestHelpers.Json("""{"type":"service_account"}"""),
                GcpCredentialsJsonIsBase64Encoded = true,
                GcpProjectId = "proj"
            }] = [StorageSecretSplitter.GcpCredentialsJson, StorageSecretSplitter.GcpCredentialsJsonIsBase64Encoded]
        };

        foreach (var testCase in cases)
        {
            var original = testCase.Key;
            var expectedSecretKeys = testCase.Value;

            var (secrets, stored) = StorageSecretSplitter.Split(original);
            secrets.Keys.OrderBy(x => x).ShouldBe(expectedSecretKeys.OrderBy(x => x));
            var storedJson = StorageTestHelpers.SerializeStored(stored);

            foreach (var secret in secrets.Values)
            {
                storedJson.Contains($"\"{secret}\"").ShouldBeFalse();
            }

            var hydrated = StorageSecretSplitter.Hydrate(stored, secrets);
            StorageTestHelpers.Serialize(hydrated).ShouldBe(StorageTestHelpers.Serialize(original));
        }
    }

    [Test]
    public void Hydrate_S3_Throws_When_Access_Key_Is_Missing()
    {
        var stored = new S3CompatibleObjectStorageStored
        {
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            Region = "us-west-2"
        };

        Should.Throw<InvalidOperationException>(() =>
            StorageSecretSplitter.Hydrate(stored, new Dictionary<string, string>
            {
                [StorageSecretSplitter.S3SecretKeyId] = "secret"
            }));
    }

    [Test]
    public void Hydrate_S3_Throws_When_Secret_Key_Is_Missing()
    {
        var stored = new S3CompatibleObjectStorageStored
        {
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            Region = "us-west-2"
        };

        Should.Throw<InvalidOperationException>(() =>
            StorageSecretSplitter.Hydrate(stored, new Dictionary<string, string>
            {
                [StorageSecretSplitter.S3AccessKeyId] = "access"
            }));
    }

    [Test]
    public void Hydrate_Gcs_Preserves_Base64_Flag()
    {
        var stored = new GoogleCloudStorageObjectStorageStored
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson,
            GcpProjectId = "proj"
        };

        var hydrated = StorageSecretSplitter.Hydrate(stored, new Dictionary<string, string>
        {
            [StorageSecretSplitter.GcpCredentialsJson] = JsonSerializer.Serialize(new { client_email = "svc@example.test" }),
            [StorageSecretSplitter.GcpCredentialsJsonIsBase64Encoded] = "true"
        });

        hydrated.ShouldBeOfType<GoogleCloudStorageObjectStorageParameters>()
            .GcpCredentialsJsonIsBase64Encoded.ShouldBeTrue();
    }
}
