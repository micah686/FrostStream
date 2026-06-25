using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

public sealed class OpenFgaTupleWriterTests
{
    [Test]
    public async Task Reconciles_Adding_New_And_Deleting_Revoked_Groups()
    {
        // The store already has the user in `group:old`; the new desired set is `group:new`.
        var handler = new StubHttpMessageHandler(request => request.Path.EndsWith("/read")
            ? StubHttpMessageHandler.Json("""{"tuples":[{"key":{"object":"group:old"}}]}""")
            : StubHttpMessageHandler.Json("{}"));
        var writer = Build(handler, storeId: "store-1");

        await writer.SyncUserGroupsAsync("abc", ["new"]);

        var writes = handler.Requests.Where(r => r.Path.EndsWith("/write")).ToArray();
        writes.Length.ShouldBe(2);
        writes.ShouldContain(r => r.Body!.Contains("\"writes\"") && r.Body.Contains("group:new"));
        writes.ShouldContain(r => r.Body!.Contains("\"deletes\"") && r.Body.Contains("group:old"));
    }

    [Test]
    public async Task Skips_Group_Names_That_Are_Not_Valid_OpenFga_Object_Ids()
    {
        var handler = new StubHttpMessageHandler(request => request.Path.EndsWith("/read")
            ? StubHttpMessageHandler.Json("""{"tuples":[]}""")
            : StubHttpMessageHandler.Json("{}"));
        var writer = Build(handler, storeId: "store-1");

        // "authentik Admins" contains a space and can never be an OpenFGA object id.
        await writer.SyncUserGroupsAsync("abc", ["authentik Admins", "admins"]);

        var writes = handler.Requests.Where(r => r.Path.EndsWith("/write")).ToArray();
        writes.ShouldHaveSingleItem();
        writes[0].Body!.ShouldContain("group:admins");
        writes[0].Body!.ShouldNotContain("authentik");
        writes[0].Body!.ShouldContain("\"writes\"");
        writes.ShouldNotContain(r => r.Body!.Contains("\"deletes\""));
    }

    [Test]
    public async Task Does_Nothing_When_Store_Not_Resolved()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}"));
        var writer = Build(handler, storeId: null);

        await writer.SyncUserGroupsAsync("abc", ["admins"]);

        handler.Requests.ShouldBeEmpty();
    }

    private static OpenFgaTupleWriter Build(StubHttpMessageHandler handler, string? storeId)
        => new(
            new HttpClient(handler),
            Options.Create(new OpenFgaOptions { Endpoint = "http://openfga.test" }),
            new OpenFgaRuntimeState { StoreId = storeId },
            NullLogger<OpenFgaTupleWriter>.Instance);
}
