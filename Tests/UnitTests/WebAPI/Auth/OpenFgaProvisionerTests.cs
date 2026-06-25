using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

public sealed class OpenFgaProvisionerTests
{
    [Test]
    public async Task Does_Nothing_When_AutoProvision_Is_Disabled()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}"));
        var state = new OpenFgaRuntimeState();
        var provisioner = Build(handler, state, new OpenFgaOptions
        {
            Endpoint = "http://openfga.test",
            AutoProvision = false
        });

        await RunAsync(provisioner);

        handler.Requests.ShouldBeEmpty();
        state.IsReady.ShouldBeFalse();
    }

    [Test]
    public async Task Does_Nothing_When_Endpoint_Is_Not_Configured()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}"));
        var state = new OpenFgaRuntimeState();
        var provisioner = Build(handler, state, new OpenFgaOptions { Endpoint = "" });

        await RunAsync(provisioner);

        handler.Requests.ShouldBeEmpty();
        state.IsReady.ShouldBeFalse();
    }

    [Test]
    public async Task Creates_Store_Model_And_Seeds_Bootstrap_Tuples()
    {
        var handler = new StubHttpMessageHandler(Respond);
        var state = new OpenFgaRuntimeState();
        var provisioner = Build(handler, state, new OpenFgaOptions
        {
            Endpoint = "http://openfga.test",
            BootstrapAdminGroup = "admins"
        });

        await RunAsync(provisioner);
        await WaitUntilProvisionedAsync(state);

        state.StoreId.ShouldBe("store-1");
        state.AuthorizationModelId.ShouldBe("model-1");

        var writes = handler.Requests.Where(r => r.Path.EndsWith("/write")).ToArray();
        writes.ShouldNotBeEmpty();
        // The lock-out guard tuple grants the bootstrap admin group the :all bundle.
        var allBundle = AuthConstants.CapabilityGroupObject(AuthConstants.AllBundle);
        writes.ShouldContain(r => r.Body!.Contains("group:admins#member") && r.Body.Contains(allBundle));
    }

    [Test]
    public async Task Honors_A_Preconfigured_Store_Id()
    {
        var handler = new StubHttpMessageHandler(Respond);
        var state = new OpenFgaRuntimeState { StoreId = "preset-store" };
        var provisioner = Build(handler, state, new OpenFgaOptions { Endpoint = "http://openfga.test" });

        await RunAsync(provisioner);
        await WaitUntilProvisionedAsync(state);

        state.StoreId.ShouldBe("preset-store");
        state.AuthorizationModelId.ShouldBe("model-1");
        // No store list/create when the id is already known.
        handler.Requests.ShouldNotContain(r => r.Path.EndsWith("/stores"));
    }

    private static HttpResponseMessage Respond(RecordedRequest request)
    {
        if (request.Path.Contains("/authorization-models"))
        {
            return request.Method == HttpMethod.Get
                ? StubHttpMessageHandler.Json("""{"authorization_models":[]}""")
                : StubHttpMessageHandler.Json("""{"authorization_model_id":"model-1"}""");
        }

        if (request.Path.EndsWith("/stores"))
        {
            return request.Method == HttpMethod.Get
                ? StubHttpMessageHandler.Json("""{"stores":[]}""")
                : StubHttpMessageHandler.Json("""{"id":"store-1"}""");
        }

        // /write
        return StubHttpMessageHandler.Json("{}");
    }

    private static OpenFgaProvisioner Build(StubHttpMessageHandler handler, OpenFgaRuntimeState state, OpenFgaOptions options)
        => new(
            new SingleClientFactory(new HttpClient(handler)),
            Options.Create(options),
            state,
            NullLogger<OpenFgaProvisioner>.Instance);

    private static async Task RunAsync(OpenFgaProvisioner provisioner)
    {
        await ((IHostedService)provisioner).StartAsync(CancellationToken.None);
    }

    private static async Task WaitUntilProvisionedAsync(OpenFgaRuntimeState state)
    {
        // Wait for the full flow (store + model + bootstrap tuples) to finish, which the model id
        // signals. IsReady alone is true the instant a store id is known, even with a preset store.
        for (var attempt = 0; attempt < 100 && string.IsNullOrEmpty(state.AuthorizationModelId); attempt++)
        {
            await Task.Delay(20);
        }

        state.AuthorizationModelId.ShouldNotBeNullOrEmpty();
    }
}
