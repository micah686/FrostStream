using IntegrationTests.Infrastructure;
using Shouldly;
using Shared.Storage;
using System.Net;
using System.Net.Http.Json;
using TUnit.Core;
using WebAPI.Features.Storage.Models;

namespace IntegrationTests.Storage;

public class StorageValidationTests
{
    private static readonly StorageStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static StorageValidationTests()
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
    public async Task Invalid_Key_Returns_400()
    {
        var response = await Fixture.Client.PostAsJsonAsync("/api/storage/local/create", new LocalStorageUpsertRequest
        {
            Key = "Bad_Key",
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/a"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Missing_S3_Region_Returns_400_With_Validation_Message()
    {
        var response = await Fixture.CreateS3Async(new S3CompatibleObjectStorageUpsertRequest
        {
            Key = "s3-a",
            Provider = S3CompatibleObjectStorageProvider.AwsS3,
            BucketName = "bucket",
            AccessKeyId = "access",
            SecretKeyId = "secret"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("region is required for AwsS3 and DigitalOceanSpaces.");
    }

    [Test]
    public async Task Missing_Azure_Sas_Secret_Returns_400_With_Validation_Message()
    {
        var response = await Fixture.CreateAzureAsync(new AzureBlobObjectStorageUpsertRequest
        {
            Key = "azure-a",
            CredentialMode = AzureBlobCredentialMode.SasUrl,
            ContainerName = "container"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("azureSasUrlSecretId is required when credentialMode is SasUrl.");
    }
}
