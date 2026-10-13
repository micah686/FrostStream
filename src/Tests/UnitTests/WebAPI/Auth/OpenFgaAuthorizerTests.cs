using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

public sealed class OpenFgaAuthorizerTests
{
    private static readonly FrostStreamAuthorizationCheck Check = new(
        "user:abc",
        AuthConstants.InvokeRelation,
        AuthConstants.EndpointObject("downloads.create"));

    [Test]
    public async Task Denies_Without_Calling_OpenFga_When_Store_Not_Resolved()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"allowed":true}"""));
        // Endpoint configured but the provisioner has not yet published a store id.
        var authorizer = Build(handler, endpoint: "http://openfga.test", storeId: null);

        var decision = await authorizer.CheckAsync(Check);

        decision.Allowed.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task Permits_When_OpenFga_Allows_And_Sends_The_Check_Tuple()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"allowed":true}"""));
        var authorizer = Build(handler, endpoint: "http://openfga.test", storeId: "store-1");

        var decision = await authorizer.CheckAsync(Check);

        decision.Allowed.ShouldBeTrue();
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Path.ShouldBe("/stores/store-1/check");
        request.Body.ShouldNotBeNull();
        request.Body!.ShouldContain("user:abc");
        request.Body.ShouldContain(AuthConstants.InvokeRelation);
        request.Body.ShouldContain(AuthConstants.EndpointObject("downloads.create"));
    }

    [Test]
    public async Task Denies_When_OpenFga_Returns_Not_Allowed()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"allowed":false}"""));
        var authorizer = Build(handler, endpoint: "http://openfga.test", storeId: "store-1");

        (await authorizer.CheckAsync(Check)).Allowed.ShouldBeFalse();
    }

    [Test]
    public async Task Denies_When_OpenFga_Returns_An_Error_Status()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json("""{"code":"boom"}""", HttpStatusCode.InternalServerError));
        var authorizer = Build(handler, endpoint: "http://openfga.test", storeId: "store-1");

        (await authorizer.CheckAsync(Check)).Allowed.ShouldBeFalse();
    }

    private static OpenFgaAuthorizer Build(StubHttpMessageHandler handler, string endpoint, string? storeId)
    {
        var state = new OpenFgaRuntimeState { StoreId = storeId };
        return new OpenFgaAuthorizer(
            new HttpClient(handler),
            Options.Create(new OpenFgaOptions { Endpoint = endpoint }),
            state,
            NullLogger<OpenFgaAuthorizer>.Instance);
    }
}
