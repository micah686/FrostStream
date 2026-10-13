using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI.Auth;

public sealed class OpenFgaBundleManagementServiceTests
{
    [Test]
    public async Task ListBundles_Uses_Filtered_Reads_For_Memberships_And_Grants()
    {
        var bundleId = "user.family";
        var bundleObject = AuthConstants.CapabilityGroupObject(bundleId);
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Body!.Contains($"\"relation\":\"{AuthConstants.BundleRelation}\""))
            {
                return StubHttpMessageHandler.Json($$"""
                    {
                      "tuples": [
                        {
                          "key": {
                            "user": "{{bundleObject}}",
                            "relation": "{{AuthConstants.BundleRelation}}",
                            "object": "{{AuthConstants.EndpointObject("media.stream")}}"
                          }
                        }
                      ],
                      "continuation_token": ""
                    }
                    """);
            }

            return StubHttpMessageHandler.Json($$"""
                {
                  "tuples": [
                    {
                      "key": {
                        "user": "group:family#member",
                        "relation": "{{AuthConstants.GranteeRelation}}",
                        "object": "{{bundleObject}}"
                      }
                    }
                  ],
                  "continuation_token": ""
                }
                """);
        });
        var service = new OpenFgaBundleManagementService(
            new HttpClient(handler),
            Options.Create(new OpenFgaOptions { Endpoint = "http://openfga.test" }),
            new OpenFgaRuntimeState { StoreId = "store-1", AuthorizationModelId = "model-1" },
            NullLogger<OpenFgaBundleManagementService>.Instance);

        var result = await service.ListBundlesAsync(CancellationToken.None);

        result.Status.ShouldBe(BundleOpStatus.Ok);
        var bundle = result.Value.ShouldNotBeNull().ShouldHaveSingleItem();
        bundle.Id.ShouldBe(bundleId);
        bundle.Endpoints.ShouldBe(["media.stream"]);
        var grant = bundle.Grants.ShouldHaveSingleItem();
        grant.Type.ShouldBe(BundleManagementValidation.GranteeTypeGroup);
        grant.Id.ShouldBe("family");

        var reads = handler.Requests.ToArray();
        reads.ShouldAllBe(read => read.Path.EndsWith("/read"));
        reads.ShouldAllBe(read => read.Body.ShouldNotBeNull().Contains("\"tuple_key\""));
        var membershipReads = reads.Where(read =>
            read.Body!.Contains($"\"relation\":\"{AuthConstants.BundleRelation}\"")).ToArray();
        membershipReads.Length.ShouldBe(EndpointCatalog.Endpoints.Count);
        membershipReads.ShouldAllBe(read =>
            read.Body!.Contains("\"object\":\"endpoint:") &&
            !read.Body.Contains($"\"object\":\"{AuthConstants.EndpointObjectPrefix}\""));
        reads.ShouldContain(read =>
            read.Body!.Contains($"\"relation\":\"{AuthConstants.GranteeRelation}\"") &&
            read.Body.Contains($"\"object\":\"{bundleObject}\""));
    }
}
