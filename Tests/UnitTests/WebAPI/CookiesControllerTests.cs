using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Secrets;
using Shouldly;
using TUnit.Core;
using WebAPI.Controllers;

namespace UnitTests.WebAPI;

public sealed class CookiesControllerTests
{
    [Test]
    public async Task Upsert_Writes_Cookie_Content_To_Secret_Path()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store);

        var result = await controller.Upsert("member-cookie", new CookieUpsertRequest
        {
            Content = "# Netscape HTTP Cookie File"
        }, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CookieResponse>().Key.ShouldBe("member-cookie");
        await store.Received(1).WriteAsync(
            "cookies/member-cookie",
            Arg.Is<IReadOnlyDictionary<string, string>>(x => x["content"] == "# Netscape HTTP Cookie File"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upsert_Rejects_Invalid_Key_And_Does_Not_Write()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store);

        var result = await controller.Upsert("Bad_Key", new CookieUpsertRequest
        {
            Content = "content"
        }, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await store.DidNotReceive().WriteAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Get_Returns_Metadata_When_Content_Field_Exists()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store);
        store.ReadAsync("cookies/member-cookie", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["content"] = "secret cookie body" });

        var result = await controller.Get("member-cookie", CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CookieResponse>().Key
            .ShouldBe("member-cookie");
    }

    [Test]
    public async Task Get_Returns_NotFound_When_Secret_Is_Missing_Content()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store);
        store.ReadAsync("cookies/member-cookie", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        var result = await controller.Get("member-cookie", CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task Delete_Returns_502_When_Secret_Store_Fails()
    {
        var store = Substitute.For<ISecretStore>();
        var controller = CreateController(store);
        store.DeleteAsync("cookies/member-cookie", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("store unavailable"));

        var result = await controller.Delete("member-cookie", CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    private static CookiesController CreateController(ISecretStore store)
        => new(store, Substitute.For<ILogger<CookiesController>>());
}
