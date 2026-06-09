using System.Reflection;
using System.Text.Json;
using Shouldly;
using Shared.Storage;
using TUnit.Core;

namespace UnitTests.Storage;

public class FluentStorageProviderTests
{
    private static readonly MethodInfo BuildConnectionStringMethod =
        typeof(FluentStorageProvider).GetMethod("BuildConnectionString", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildConnectionString not found.");

    [Test]
    public void BuildConnectionString_Formats_Disk_Storage()
    {
        Build(StorageMethod.Local, new PosixLocalStorageParameters
        {
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/storage"
        }).ShouldBe("disk://path=/mnt/storage");
    }

    [Test]
    [NotInParallel("FrostStreamStorageRootEnvironment")]
    public void BuildConnectionString_Requires_Configured_Shared_Storage_Root()
    {
        var previousRoot = Environment.GetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName, null);

        try
        {
            var exception = Should.Throw<TargetInvocationException>(() =>
                Build(StorageMethod.Local, new PosixLocalStorageParameters
                {
                    Protocol = LocalStorageProtocol.Local,
                    Path = LocalStoragePathResolver.StorageRootToken
                }));

            exception.InnerException.ShouldBeOfType<InvalidOperationException>()
                .Message.ShouldContain(LocalStoragePathResolver.EnvironmentVariableName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalStoragePathResolver.EnvironmentVariableName, previousRoot);
        }
    }

    [Test]
    public void BuildConnectionString_Formats_Network_Storage()
    {
        Build(StorageMethod.Network, new StreamingNetworkStorageParameters
        {
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "example.test",
            Port = 22,
            Username = "micah",
            Password = "pw",
            PrivateKey = "private",
            PublicKey = "public",
            BasePath = "/upload"
        }).ShouldBe("sftp://host=example.test;port=22;user=micah;password=pw;privateKey=private;publicKey=public;path=/upload");
    }

    [Test]
    public void BuildConnectionString_Formats_S3_Storage_With_Flags()
    {
        Build(StorageMethod.ObjectStorage, new S3CompatibleObjectStorageParameters
        {
            Provider = S3CompatibleObjectStorageProvider.MinIo,
            BucketName = "bucket",
            Region = "us-east-1",
            Endpoint = "https://minio.example.test",
            AccessKeyId = "access",
            SecretKeyId = "secret",
            ForcePathStyle = true,
            UseSsl = false
        }).ShouldBe("aws.s3://bucket=bucket;region=us-east-1;keyId=access;key=secret;serviceUrl=https://minio.example.test;forcePathStyle=true;useSsl=false");
    }

    [Test]
    public void BuildConnectionString_Formats_Azure_AccountKey_Mode()
    {
        Build(StorageMethod.ObjectStorage, new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.AccountKey,
            ContainerName = "container",
            AzureAccountName = "account",
            AzureAccountKeySecretId = "secret"
        }).ShouldBe("azure.blob://container=container;account=account;key=secret");
    }

    [Test]
    public void BuildConnectionString_Formats_Azure_ConnectionString_Mode()
    {
        Build(StorageMethod.ObjectStorage, new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.ConnectionString,
            ContainerName = "container",
            AzureConnectionStringSecretId = "UseDevelopmentStorage=true"
        }).ShouldBe("azure.blob://container=container;connectionString=UseDevelopmentStorage=true");
    }

    [Test]
    public void BuildConnectionString_Formats_Azure_SasUrl_Mode()
    {
        Build(StorageMethod.ObjectStorage, new AzureBlobObjectStorageParameters
        {
            CredentialMode = AzureBlobCredentialMode.SasUrl,
            ContainerName = "container",
            AzureSasUrlSecretId = "https://example.test/sas"
        }).ShouldBe("azure.blob://container=container;sasUrl=https://example.test/sas");
    }

    [Test]
    public void BuildConnectionString_Formats_Gcs_CredentialsJson_Mode()
    {
        Build(StorageMethod.ObjectStorage, new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson,
            GcpCredentialsJson = StorageTestHelpers.Json("""{"client_email":"svc@example.test"}"""),
            GcpProjectId = "proj"
        }).ShouldBe("""google.storage://bucket=bucket;projectId=proj;cred={"client_email":"svc@example.test"}""");
    }

    [Test]
    public void BuildConnectionString_Formats_Gcs_CredentialsFile_Mode()
    {
        Build(StorageMethod.ObjectStorage, new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsFilePath,
            GcpCredentialsFilePath = "/tmp/gcp.json",
            GcpProjectId = "proj"
        }).ShouldBe("google.storage://bucket=bucket;projectId=proj;credFile=/tmp/gcp.json");
    }

    [Test]
    public void BuildConnectionString_Formats_Gcs_WorkloadIdentity_And_DefaultCredentials_Modes()
    {
        Build(StorageMethod.ObjectStorage, new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.WorkloadIdentity,
            GcpProjectId = "proj"
        }).ShouldBe("google.storage://bucket=bucket;projectId=proj;auth=workloadIdentity");

        Build(StorageMethod.ObjectStorage, new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.DefaultCredentials,
            GcpProjectId = "proj"
        }).ShouldBe("google.storage://bucket=bucket;projectId=proj;auth=defaultCredentials");
    }

    [Test]
    public void BuildConnectionString_Decodes_Base64_Gcs_Credentials()
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"client_email":"svc@example.test"}"""));

        Build(StorageMethod.ObjectStorage, new GoogleCloudStorageObjectStorageParameters
        {
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson,
            GcpCredentialsJson = JsonSerializer.SerializeToElement(base64),
            GcpCredentialsJsonIsBase64Encoded = true
        }).ShouldBe("""google.storage://bucket=bucket;cred={"client_email":"svc@example.test"}""");
    }

    private static string Build(StorageMethod method, StorageParametersBase parameters)
    {
        return (string)(BuildConnectionStringMethod.Invoke(
            null,
            [method, StorageParametersSerializer.Serialize(method, parameters)])
            ?? throw new InvalidOperationException("Connection string not returned."));
    }
}
