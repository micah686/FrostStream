using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
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

        var modelWrite = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Path.Contains("/authorization-models"));
        var modelBody = modelWrite.Body!;
        modelBody.ShouldContain("\"type\": \"access_policy\"");
        modelBody.ShouldContain("\"relation\": \"policy\"");

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

    [Test]
    public async Task Honors_A_Preconfigured_Authorization_Model_Id()
    {
        var handler = new StubHttpMessageHandler(Respond);
        var state = new OpenFgaRuntimeState
        {
            StoreId = "preset-store",
            AuthorizationModelId = "preset-model"
        };
        var provisioner = Build(handler, state, new OpenFgaOptions { Endpoint = "http://openfga.test" });

        await RunAsync(provisioner);
        await WaitUntilWriteAsync(handler);

        state.AuthorizationModelId.ShouldBe("preset-model");
        handler.Requests.ShouldNotContain(r => r.Path.Contains("/authorization-models"));
        handler.Requests.ShouldContain(r => r.Path.EndsWith("/write"));
    }

    [Test]
    public async Task Reuses_An_Existing_Model_With_The_Same_Content_Hash()
    {
        var existingModel = JsonNode.Parse(OpenFgaModel.Json)!.AsObject();
        existingModel["id"] = "model-existing";
        existingModel["created_at"] = "2026-07-23T00:00:00Z";
        var modelList = new JsonObject
        {
            ["authorization_models"] = new JsonArray(existingModel),
            ["continuation_token"] = ""
        }.ToJsonString();

        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Path.Contains("/authorization-models"))
            {
                return request.Method == HttpMethod.Get
                    ? StubHttpMessageHandler.Json(modelList)
                    : throw new InvalidOperationException("A matching authorization model must not be rewritten.");
            }

            if (request.Path.EndsWith("/stores"))
            {
                return request.Method == HttpMethod.Get
                    ? StubHttpMessageHandler.Json("""{"stores":[{"id":"store-1","name":"froststream"}]}""")
                    : throw new InvalidOperationException("The existing store must be reused.");
            }

            return StubHttpMessageHandler.Json("{}");
        });
        var state = new OpenFgaRuntimeState();
        var provisioner = Build(handler, state, new OpenFgaOptions
        {
            Endpoint = "http://openfga.test",
            StoreName = "froststream"
        });

        await RunAsync(provisioner);
        await WaitUntilWriteAsync(handler);

        state.StoreId.ShouldBe("store-1");
        state.AuthorizationModelId.ShouldBe("model-existing");
        handler.Requests.ShouldNotContain(request =>
            request.Method == HttpMethod.Post && request.Path.Contains("/authorization-models"));
    }

    [Test]
    public void Model_Content_Hash_Ignores_Server_Assigned_Model_Metadata()
    {
        var storedModel = JsonNode.Parse(OpenFgaModel.Json)!.AsObject();
        storedModel["id"] = "model-1";
        storedModel["created_at"] = "2026-07-23T00:00:00Z";

        OpenFgaModel.ComputeContentHash(storedModel.ToJsonString())
            .ShouldBe(OpenFgaModel.ContentHash);
    }

    [Test]
    public void Model_Content_Hash_Ignores_OpenFga_Defaults_Added_On_Read()
    {
        var storedModel = JsonNode.Parse(OpenFgaModel.Json)!.AsObject();
        storedModel["id"] = "model-1";
        storedModel["created_at"] = "2026-07-23T00:00:00Z";
        storedModel["conditions"] = new JsonObject();

        foreach (var typeDefinition in storedModel["type_definitions"]!.AsArray().OfType<JsonObject>())
        {
            typeDefinition.TryAdd("relations", new JsonObject());
            typeDefinition.TryAdd("metadata", null);

            if (typeDefinition["metadata"]?["relations"] is JsonObject relationMetadata)
            {
                foreach (var relation in relationMetadata.Select(entry => entry.Value).OfType<JsonObject>())
                {
                    relation["module"] = "";
                    relation["source_info"] = null;
                    if (relation["directly_related_user_types"] is JsonArray userTypes)
                    {
                        foreach (var userType in userTypes.OfType<JsonObject>())
                        {
                            userType["condition"] = "";
                        }
                    }
                }
            }
        }

        OpenFgaModel.ComputeContentHash(storedModel.ToJsonString())
            .ShouldBe(OpenFgaModel.ContentHash);
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

    private static async Task WaitUntilWriteAsync(StubHttpMessageHandler handler)
    {
        for (var attempt = 0; attempt < 100 && !handler.Requests.Any(r => r.Path.EndsWith("/write")); attempt++)
        {
            await Task.Delay(20);
        }
    }
}
