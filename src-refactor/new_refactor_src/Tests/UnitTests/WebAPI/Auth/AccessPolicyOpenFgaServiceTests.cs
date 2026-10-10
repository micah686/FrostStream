using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

public sealed class AccessPolicyOpenFgaServiceTests
{
    [Test]
    public async Task Synchronize_Writes_Bundle_And_Principal_Relationships()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.Path.EndsWith("/read")
                ? StubHttpMessageHandler.Json("""{"tuples":[],"continuation_token":""}""")
                : StubHttpMessageHandler.Json("{}"));
        var policyId = Guid.Parse("2b7319c4-e0ae-4b89-bb71-af4d191a3bde");
        var service = new OpenFgaAccessPolicyService(
            new HttpClient(handler),
            Options.Create(new OpenFgaOptions { Endpoint = "http://openfga.test" }),
            new OpenFgaRuntimeState { StoreId = "store-1", AuthorizationModelId = "model-1" },
            NullLogger<OpenFgaAccessPolicyService>.Instance);

        var result = await service.SynchronizeAsync(new AccessPolicyDto
        {
            PolicyId = policyId,
            Name = "Family access",
            Enabled = true,
            BundleIds = ["watching", "user.family"],
            Assignments =
            [
                new AccessPolicyAssignmentDto { Type = "user", Id = "subject-1" },
                new AccessPolicyAssignmentDto { Type = "group", Id = "family" }
            ]
        }, CancellationToken.None);

        result.Status.ShouldBe(BundleOpStatus.Ok);
        var reads = handler.Requests.Where(x => x.Path.EndsWith("/read")).ToArray();
        reads.Length.ShouldBe(2);
        reads.ShouldAllBe(read => read.Body.ShouldNotBeNull().Contains("\"tuple_key\""));
        reads.ShouldContain(read =>
            read.Body!.Contains($"\"object\":\"{AuthConstants.AccessPolicyObject(policyId)}\"") &&
            read.Body.Contains($"\"relation\":\"{AuthConstants.GranteeRelation}\""));
        reads.ShouldContain(read =>
            read.Body!.Contains($"\"user\":\"{AuthConstants.AccessPolicyObject(policyId)}\"") &&
            read.Body.Contains($"\"relation\":\"{AuthConstants.PolicyRelation}\"") &&
            read.Body.Contains($"\"object\":\"{AuthConstants.CapabilityGroupObjectPrefix}\""));
        var write = handler.Requests.Single(x => x.Path.EndsWith("/write"));
        var body = write.Body!;
        var policyObject = AuthConstants.AccessPolicyObject(policyId);
        body.ShouldContain(policyObject);
        body.ShouldContain(AuthConstants.CapabilityGroupObject("watching"));
        body.ShouldContain(AuthConstants.CapabilityGroupObject("user.family"));
        body.ShouldContain("\"relation\":\"policy\"");
        body.ShouldContain("user:subject-1");
        body.ShouldContain("group:family#member");
        body.ShouldContain("\"authorization_model_id\":\"model-1\"");
    }

    [Test]
    public async Task ListUserGroups_Uses_A_Filtered_OpenFga_Read()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""
            {
              "tuples": [
                { "key": { "user": "user:subject-1", "relation": "member", "object": "group:admins" } }
              ],
              "continuation_token": ""
            }
            """));
        var service = new OpenFgaAccessPolicyService(
            new HttpClient(handler),
            Options.Create(new OpenFgaOptions { Endpoint = "http://openfga.test" }),
            new OpenFgaRuntimeState { StoreId = "store-1", AuthorizationModelId = "model-1" },
            NullLogger<OpenFgaAccessPolicyService>.Instance);

        var result = await service.ListUserGroupsAsync("subject-1", CancellationToken.None);

        result.Status.ShouldBe(BundleOpStatus.Ok);
        result.Value.ShouldBe(["admins"]);
        var read = handler.Requests.ShouldHaveSingleItem();
        read.Path.ShouldEndWith("/read");
        var readBody = read.Body.ShouldNotBeNull();
        readBody.ShouldContain("\"tuple_key\"");
        readBody.ShouldContain("\"user\":\"user:subject-1\"");
        readBody.ShouldContain("\"relation\":\"member\"");
        readBody.ShouldContain("\"object\":\"group:\"");
    }

    [Test]
    public async Task ListEffectiveEndpoints_Uses_ListObjects_And_Normalizes_Endpoint_Ids()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""
            { "objects": ["endpoint:media.stream", "endpoint:metadata.list"] }
            """));
        var service = new OpenFgaAccessPolicyService(
            new HttpClient(handler),
            Options.Create(new OpenFgaOptions { Endpoint = "http://openfga.test" }),
            new OpenFgaRuntimeState { StoreId = "store-1", AuthorizationModelId = "model-1" },
            NullLogger<OpenFgaAccessPolicyService>.Instance);

        var result = await service.ListEffectiveEndpointsAsync("group", "family", CancellationToken.None);

        result.Status.ShouldBe(BundleOpStatus.Ok);
        result.Value.ShouldBe(["media.stream", "metadata.list"]);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Path.ShouldEndWith("/list-objects");
        var requestBody = request.Body.ShouldNotBeNull();
        requestBody.ShouldContain("\"type\":\"endpoint\"");
        requestBody.ShouldContain("\"relation\":\"invoke\"");
        requestBody.ShouldContain("\"user\":\"group:family#member\"");
    }
}
