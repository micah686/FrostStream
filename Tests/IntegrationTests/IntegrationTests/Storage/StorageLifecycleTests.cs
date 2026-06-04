using IntegrationTests.Infrastructure;
using Shouldly;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using System.Net;
using System.Net.Http.Json;
using TUnit.Core;
using WebAPI.Features.Storage.Models;

namespace IntegrationTests.Storage;

public class StorageLifecycleTests
{
    private static readonly StorageStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static StorageLifecycleTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task ResetAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        await Fixture.ResetAsync();
    }

    [After(Test)]
    public void Release()
    {
        Gate.Release();
    }

    [Test]
    public async Task Local_Storage_Lifecycle_Works()
    {
        var created = await Fixture.CreateLocalAsync("local-a", "/mnt/local-a");
        var entity = await Fixture.FindStorageAsync("local-a");

        entity.ShouldNotBeNull();
        entity.Method.ShouldBe(StorageMethod.Local);
        entity.Local!.Path.ShouldBe("/mnt/local-a");
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("local-a"))).ShouldBeNull();

        var getResponse = await Fixture.Client.GetAsync("/api/storage/local-a");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updateResponse = await Fixture.Client.PutAsJsonAsync("/api/storage/local/update/local-a", new LocalStorageUpdateRequest
        {
            Description = "updated",
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/local-b"
        });
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updatedEntity = await Fixture.FindStorageAsync("local-a");
        updatedEntity!.LastUpdated.ShouldNotBeNull();
        updatedEntity.Local!.Path.ShouldBe("/mnt/local-b");

        var deleteResponse = await Fixture.Client.DeleteAsync("/api/storage/delete/local-a");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Fixture.FindStorageAsync("local-a")).ShouldBeNull();
    }

    [Test]
    public async Task Network_Storage_Lifecycle_Works()
    {
        var response = await Fixture.CreateNetworkAsync(new NetworkStorageUpsertRequest
        {
            Key = "network-a",
            Description = "desc",
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "example.test",
            Port = 22,
            Username = "micah",
            Password = "pw",
            BasePath = "/upload"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entity = await Fixture.FindStorageAsync("network-a");
        entity!.Network!.Host.ShouldBe("example.test");
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("network-a")))!.ShouldContainKey(StorageSecretSplitter.NetworkPassword);

        var deleteResponse = await Fixture.Client.DeleteAsync("/api/storage/delete/network-a");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("network-a"))).ShouldBeNull();
    }

    [Test]
    public async Task S3_Storage_Lifecycle_Works()
    {
        var response = await Fixture.CreateS3Async(new S3CompatibleObjectStorageUpsertRequest
        {
            Key = "s3-a",
            Description = "desc",
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            Region = "us-west-2",
            AccessKeyId = "access",
            SecretKeyId = "secret",
            SessionTokenSecretId = "session",
            ForcePathStyle = true,
            UseSsl = false
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entity = await Fixture.FindStorageAsync("s3-a");
        entity!.ObjectS3Compatible!.HasSessionToken.ShouldBeTrue();
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("s3-a")))!.ShouldContainKey(StorageSecretSplitter.S3AccessKeyId);

        var deleteResponse = await Fixture.Client.DeleteAsync("/api/storage/delete/s3-a");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Azure_Storage_Lifecycle_Works()
    {
        var response = await Fixture.CreateAzureAsync(new AzureBlobObjectStorageUpsertRequest
        {
            Key = "azure-a",
            Description = "desc",
            CredentialMode = AzureBlobCredentialMode.AccountKey,
            ContainerName = "container",
            AzureAccountName = "account",
            AzureAccountKeySecretId = "secret"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entity = await Fixture.FindStorageAsync("azure-a");
        entity!.ObjectAzureBlob!.AzureAccountName.ShouldBe("account");
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("azure-a")))!.ShouldContainKey(StorageSecretSplitter.AzureAccountKey);
    }

    [Test]
    public async Task Gcs_Storage_Lifecycle_Works()
    {
        var response = await Fixture.CreateGcsAsync(new GoogleCloudStorageObjectStorageUpsertRequest
        {
            Key = "gcs-a",
            BucketName = "bucket",
            CredentialMode = GoogleCloudStorageCredentialMode.CredentialsJson,
            GcpCredentialsJson = StorageStackFixture.JsonOptions.GetType() is not null
                ? System.Text.Json.JsonSerializer.SerializeToElement(new { client_email = "svc@example.test" })
                : throw new InvalidOperationException(),
            GcpProjectId = "proj"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entity = await Fixture.FindStorageAsync("gcs-a");
        entity!.ObjectGoogleCloudStorage!.BucketName.ShouldBe("bucket");
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("gcs-a")))!.ShouldContainKey(StorageSecretSplitter.GcpCredentialsJson);
    }
}
