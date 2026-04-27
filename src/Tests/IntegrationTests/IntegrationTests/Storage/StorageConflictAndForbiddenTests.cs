using IntegrationTests.Infrastructure;
using Shouldly;
using Shared.Database;
using Shared.Storage;
using System.Net;
using System.Net.Http.Json;
using TUnit.Core;
using WebAPI.Controllers;

namespace IntegrationTests.Storage;

public class StorageConflictAndForbiddenTests
{
    private static readonly StorageStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static StorageConflictAndForbiddenTests()
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
    public async Task Duplicate_Key_Returns_Conflict()
    {
        (await Fixture.CreateLocalAsync("dup-a", "/mnt/a")).Key.ShouldBe("dup-a");

        var duplicate = await Fixture.Client.PostAsJsonAsync("/api/storage/local/create", new LocalStorageUpsertRequest
        {
            Key = "dup-a",
            Protocol = LocalStorageProtocol.Local,
            Path = "/mnt/b"
        });

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Default_Cannot_Be_Updated_Or_Deleted()
    {
        var update = await Fixture.Client.PutAsJsonAsync("/api/storage/local/update/default", new LocalStorageUpdateRequest
        {
            Protocol = LocalStorageProtocol.Local,
            Path = "/changed"
        });
        var delete = await Fixture.Client.DeleteAsync("/api/storage/delete/default");

        update.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Unknown_Key_Returns_NotFound_For_Get_And_Delete()
    {
        (await Fixture.Client.GetAsync("/api/storage/missing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Fixture.Client.DeleteAsync("/api/storage/delete/missing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
