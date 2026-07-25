using System.Security.Claims;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;
using WebAPI.Features.Management.Controllers;

namespace UnitTests.WebAPI;

public sealed class AccessControlControllerTests
{
    [Test]
    public async Task CreateBundle_Forwards_System_Clone_Baseline()
    {
        var (controller, _, bundles, _) = CreateController([]);
        bundles.CreateBundleAsync(
                "user.library-readers",
                Arg.Any<IReadOnlyCollection<string>>(),
                "media",
                Arg.Any<CancellationToken>())
            .Returns(BundleOpResult.Ok);

        var result = await controller.CreateBundle(
            new AccessControlCreateBundleRequest
            {
                Id = "user.library-readers",
                Name = "Library readers",
                CloneFrom = "media",
                Endpoints = ["metadata.list"]
            },
            CancellationToken.None);

        result.ShouldBeOfType<CreatedAtActionResult>();
        await bundles.Received(1).CreateBundleAsync(
            "user.library-readers",
            Arg.Is<IReadOnlyCollection<string>>(ids =>
                ids != null && ids.Count == 1 && ids.Contains("metadata.list")),
            "media",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteBundle_Returns_Conflict_While_A_Policy_References_It()
    {
        var policy = Policy("Kids", "user.kids");
        var (controller, _, bundles, _) = CreateController([policy]);

        var result = await controller.DeleteBundle("user.kids", CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
        await bundles.DidNotReceiveWithAnyArgs()
            .DeleteBundleAsync(default!, default);
    }

    [Test]
    public async Task ListBundles_Includes_Policy_Count_And_Member_Summaries()
    {
        var policy = Policy("Kids", "user.kids");
        var (controller, _, bundles, _) = CreateController([policy]);
        bundles.ListBundlesAsync(Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<BundleView>>.Ok(
            [
                new BundleView("user.kids", false, ["media.stream", "media.thumbnail"], [])
            ]));

        var result = await controller.ListBundles(CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var views = ok.Value.ShouldBeAssignableTo<IReadOnlyList<AccessControlBundleView>>();
        var view = views.ShouldHaveSingleItem();
        view.EndpointCount.ShouldBe(2);
        view.PolicyCount.ShouldBe(1);
        view.MemberPolicies.ShouldHaveSingleItem().PolicyId.ShouldBe(policy.PolicyId);
    }

    [Test]
    public async Task CreatePolicy_Returns_Accepted_When_OpenFga_Synchronization_Is_Deferred()
    {
        var (controller, bus, bundles, openFga) = CreateController([]);
        bundles.ListBundlesAsync(Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<BundleView>>.Ok(
            [
                new BundleView("media", true, ["media.stream"], [])
            ]));
        bus.RequestAsync<AccessPolicySaveRequestMessage, AccessPolicyOperationResponseMessage>(
                AccessPolicySubjects.Save,
                Arg.Any<AccessPolicySaveRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.ArgAt<AccessPolicySaveRequestMessage>(1);
                return new AccessPolicyOperationResponseMessage
                {
                    Success = true,
                    Policy = request.Policy with { Version = 1 }
                };
            });
        bus.RequestAsync<AccessPolicySetSyncRequestMessage, AccessPolicyOperationResponseMessage>(
                AccessPolicySubjects.SetSync,
                Arg.Any<AccessPolicySetSyncRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AccessPolicyOperationResponseMessage { Success = false });
        openFga.SynchronizeAsync(Arg.Any<AccessPolicyDto>(), Arg.Any<CancellationToken>())
            .Returns(BundleOpResult.Unavailable("OpenFGA is unavailable."));

        var result = await controller.CreatePolicy(
            new AccessPolicyWriteRequest
            {
                Name = "Family",
                BundleIds = ["media"]
            },
            CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        var policy = accepted.Value.ShouldBeOfType<AccessPolicyDto>();
        policy.SyncStatus.ShouldBe(AccessPolicySyncStatus.Failed);
        policy.SyncError.ShouldBe("OpenFGA is unavailable.");
    }

    [Test]
    public async Task Effective_Unions_Denies_Without_Sync_Gating_And_Explains_Endpoint_Grants()
    {
        var syncedPolicy = Policy("Endpoint access", "media") with
        {
            Assignments = [new AccessPolicyAssignmentDto { Type = "group", Id = "family" }]
        };
        var failedDenyPolicy = Policy("Block YouTube") with
        {
            SyncStatus = AccessPolicySyncStatus.Failed,
            Providers = ["YouTube"],
            Assignments = [new AccessPolicyAssignmentDto { Type = "user", Id = "subject-1" }]
        };
        var (controller, bus, bundles, openFga) = CreateController([syncedPolicy, failedDenyPolicy]);
        openFga.ListEffectiveEndpointsAsync("user", "subject-1", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Ok(["media.stream"]));
        openFga.ListUserGroupsAsync("subject-1", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Ok(["family"]));
        bundles.ListBundlesAsync(Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<BundleView>>.Ok(
            [
                new BundleView("media", true, ["media.stream"], [])
            ]));
        bus.RequestAsync<AccessPolicyEffectiveMediaRequestMessage, AccessPolicyOperationResponseMessage>(
                AccessPolicySubjects.EffectiveMedia,
                Arg.Any<AccessPolicyEffectiveMediaRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AccessPolicyOperationResponseMessage
            {
                Success = true,
                EffectiveMedia = new AccessPolicyEffectiveMediaDto
                {
                    MediaGuid = Guid.Parse("cd46731e-1d6c-47fc-849b-912592cee108"),
                    Found = true,
                    IsAllowed = false,
                    Decisions =
                    [
                        new AccessPolicyAxisDecisionDto
                        {
                            Axis = "provider",
                            Resource = "youtube",
                            Restricted = true,
                            Allowed = false,
                            DenyingPolicyIds = [failedDenyPolicy.PolicyId],
                            Reason = "An assigned policy denies provider 'youtube'."
                        }
                    ]
                }
            });

        var result = await controller.Effective(
            "user",
            "subject-1",
            "media.stream",
            Guid.Parse("cd46731e-1d6c-47fc-849b-912592cee108"),
            CancellationToken.None);

        var access = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<EffectiveAccessResponse>();
        access.PolicyIds.ShouldBe([syncedPolicy.PolicyId, failedDenyPolicy.PolicyId], ignoreOrder: true);
        access.EndpointPolicyIds.ShouldBe([syncedPolicy.PolicyId]);
        access.DenyPolicyIds.ShouldBe([failedDenyPolicy.PolicyId]);
        access.PolicyBundleIds.ShouldBe(["media"]);
        access.DeniedProviders.ShouldBe(["youtube"]);
        access.EndpointDecision.ShouldNotBeNull();
        access.EndpointDecision!.Allowed.ShouldBeTrue();
        access.EndpointDecision.GrantingPolicyIds.ShouldBe([syncedPolicy.PolicyId]);
        access.SourcePolicies.Single(policy => policy.PolicyId == failedDenyPolicy.PolicyId)
            .ContributesDenies.ShouldBeTrue();
        access.Media!.IsAllowed.ShouldBeFalse();
    }

    [Test]
    public async Task EffectiveCheck_Returns_One_Combined_Decision_With_Per_Axis_Reasons()
    {
        var (controller, bus, bundles, openFga) = CreateController([]);
        openFga.ListEffectiveEndpointsAsync("user", "subject-1", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Ok([]));
        openFga.ListUserGroupsAsync("subject-1", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Ok([]));
        bundles.ListBundlesAsync(Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<BundleView>>.Ok([]));
        bus.RequestAsync<AccessPolicyEffectiveMediaRequestMessage, AccessPolicyOperationResponseMessage>(
                AccessPolicySubjects.EffectiveMedia,
                Arg.Any<AccessPolicyEffectiveMediaRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AccessPolicyOperationResponseMessage
            {
                Success = true,
                EffectiveMedia = new AccessPolicyEffectiveMediaDto
                {
                    MediaGuid = Guid.Parse("a4232712-c240-438d-95c5-d972b61a2a84"),
                    Found = true,
                    IsAllowed = true,
                    Decisions =
                    [
                        new AccessPolicyAxisDecisionDto
                        {
                            Axis = "media",
                            Restricted = false,
                            Allowed = true,
                            Reason = "No assigned policy denies this media GUID."
                        }
                    ]
                }
            });

        var result = await controller.CheckEffectiveAccess(
            new EffectiveAccessCheckRequest
            {
                PrincipalType = "user",
                PrincipalId = "subject-1",
                EndpointId = "media.stream",
                MediaGuid = Guid.Parse("a4232712-c240-438d-95c5-d972b61a2a84")
            },
            CancellationToken.None);

        var check = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<EffectiveAccessCheckResponse>();
        check.IsAllowed.ShouldBeFalse();
        check.Decisions.Select(decision => decision.Axis).ShouldBe(["endpoint", "media"]);
        check.Decisions[0].Reason.ShouldContain("does not allow");
    }

    [Test]
    public async Task EffectiveMe_Uses_Current_Group_Claims_For_Deny_Assignment()
    {
        var policy = Policy("Family deny") with
        {
            SyncStatus = AccessPolicySyncStatus.Failed,
            Providers = ["youtube"],
            Assignments = [new AccessPolicyAssignmentDto { Type = "group", Id = "family" }]
        };
        var (controller, _, bundles, openFga) = CreateController([policy]);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(AuthConstants.SubjectClaim, "subject-me"),
                    new Claim(AuthConstants.GroupsClaim, "family")
                ], "test"))
            }
        };
        openFga.ListEffectiveEndpointsAsync("user", "subject-me", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Ok([]));
        bundles.ListBundlesAsync(Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<BundleView>>.Ok([]));

        var result = await controller.EffectiveMe(null, null, CancellationToken.None);

        var access = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<EffectiveAccessResponse>();
        access.Groups.ShouldBe(["family"]);
        access.DenyPolicyIds.ShouldBe([policy.PolicyId]);
        access.DeniedProviders.ShouldBe(["youtube"]);
        await openFga.DidNotReceiveWithAnyArgs()
            .ListUserGroupsAsync(default!, default);
    }

    [Test]
    public async Task Effective_Fails_Closed_When_Group_Membership_Cannot_Be_Resolved()
    {
        var (controller, _, _, openFga) = CreateController([]);
        openFga.ListEffectiveEndpointsAsync("user", "subject-1", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Ok([]));
        openFga.ListUserGroupsAsync("subject-1", Arg.Any<CancellationToken>())
            .Returns(BundleOpResult<IReadOnlyList<string>>.Unavailable("OpenFGA membership query failed."));

        var result = await controller.Effective(
            "user", "subject-1", null, null, CancellationToken.None);

        var unavailable = result.ShouldBeOfType<ObjectResult>();
        unavailable.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    private static (
        AccessControlController Controller,
        IMessageBus Bus,
        IBundleManagementService Bundles,
        IAccessPolicyOpenFgaService OpenFga) CreateController(IReadOnlyList<AccessPolicyDto> policies)
    {
        var bus = Substitute.For<IMessageBus>();
        bus.RequestAsync<AccessPolicyListRequestMessage, AccessPolicyOperationResponseMessage>(
                AccessPolicySubjects.List,
                Arg.Any<AccessPolicyListRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AccessPolicyOperationResponseMessage
            {
                Success = true,
                Policies = policies
            });

        var bundles = Substitute.For<IBundleManagementService>();
        var openFga = Substitute.For<IAccessPolicyOpenFgaService>();
        var controller = new AccessControlController(
            bus,
            bundles,
            Substitute.For<IDirectoryService>(),
            openFga,
            Substitute.For<ILogger<AccessControlController>>());
        return (controller, bus, bundles, openFga);
    }

    private static AccessPolicyDto Policy(string name, params string[] bundleIds)
        => new()
        {
            PolicyId = Guid.NewGuid(),
            Name = name,
            Enabled = true,
            SyncStatus = AccessPolicySyncStatus.Synced,
            BundleIds = bundleIds
        };
}
