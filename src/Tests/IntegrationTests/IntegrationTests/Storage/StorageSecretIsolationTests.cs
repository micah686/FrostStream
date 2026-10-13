using IntegrationTests.Infrastructure;
using Shouldly;
using Shared.Secrets;
using Shared.Storage;
using System.Net;
using TUnit.Core;
using WebAPI.Features.Storage.Models;

namespace IntegrationTests.Storage;

public class StorageSecretIsolationTests
{
    private static readonly StorageStackFixture Fixture = new();
    private static readonly StorageStackFixture FailingFixture = new(new FailingSaveChangesInterceptor());
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static StorageSecretIsolationTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FailingFixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task ResetAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
        await Fixture.ResetAsync();
        await FailingFixture.InitializeAsync();
        await FailingFixture.ResetAsync();
    }

    [After(Test)]
    public void Release()
    {
        Gate.Release();
    }

    [Test]
    public async Task Stored_Params_Do_Not_Contain_Secrets_And_Delete_Removes_Secret_Path()
    {
        var response = await Fixture.CreateNetworkAsync(new NetworkStorageUpsertRequest
        {
            Key = "network-a",
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "example.test",
            Username = "micah",
            Password = "pw",
            BasePath = "/upload"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entity = await Fixture.FindStorageAsync("network-a");
        entity.ShouldNotBeNull();
        entity.Network!.Username.ShouldBe("micah");
        var storedJson = System.Text.Json.JsonSerializer.Serialize(entity.StoredParameters);
        storedJson.ShouldNotContain("pw");

        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("network-a")))![StorageSecretSplitter.NetworkPassword].ShouldBe("pw");

        (await Fixture.Client.DeleteAsync("/api/storage/delete/network-a")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Fixture.SecretStore.ReadAsync(SecretPaths.ForStorage("network-a"))).ShouldBeNull();
    }

    [Test]
    public async Task Failed_Db_Write_Rolls_Back_Secret_Write()
    {
        var response = await FailingFixture.CreateNetworkAsync(new NetworkStorageUpsertRequest
        {
            Key = "rollback-a",
            Protocol = NetworkStorageProtocol.Sftp,
            Host = "example.test",
            Username = "micah",
            Password = "pw"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await FailingFixture.SecretStore.ReadAsync(SecretPaths.ForStorage("rollback-a"))).ShouldBeNull();
    }
}
