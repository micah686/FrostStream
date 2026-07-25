using DataBridge.Messaging;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class AccessPolicyDenyEvaluatorTests
{
    private static readonly Guid MediaGuid = Guid.Parse("13d99ba4-9691-4968-b686-81b9feb70f0e");

    [Test]
    public void Assigned_Media_Scope_Denies_The_Matching_Guid()
    {
        var policyId = Guid.NewGuid();

        var result = Evaluate(
            Summary(),
            Scope(policyId, mediaGuids: [MediaGuid]));

        result.IsAllowed.ShouldBeFalse();
        var decision = result.Decisions.Single(x => x.Axis == "media");
        decision.Allowed.ShouldBeFalse();
        decision.DenyingPolicyIds.ShouldBe([policyId]);
    }

    [Test]
    public void Assigned_Provider_Scope_Denies_Matching_Provider_Case_Insensitively()
    {
        var policyId = Guid.NewGuid();

        var result = Evaluate(
            Summary(providers: ["YouTube"]),
            Scope(policyId, providers: ["youtube"]));

        result.IsAllowed.ShouldBeFalse();
        result.Decisions.Single(x => x.Axis == "provider" && x.Resource == "youtube")
            .DenyingPolicyIds.ShouldBe([policyId]);
    }

    [Test]
    public void Age_Deny_Is_Inclusive_And_Does_Not_Deny_Below_The_Threshold()
    {
        var policyId = Guid.NewGuid();
        var scope = Scope(policyId, minimumAges: [18]);

        var atThreshold = Evaluate(Summary(ageLimit: 18), scope);
        var belowThreshold = Evaluate(Summary(ageLimit: 17), scope);

        atThreshold.IsAllowed.ShouldBeFalse();
        atThreshold.Decisions.Single(x => x.Axis == "age").DenyingPolicyIds.ShouldBe([policyId]);
        belowThreshold.IsAllowed.ShouldBeTrue();
    }

    [Test]
    public void Denies_Are_Unioned_Across_Assigned_Policies_And_Axes()
    {
        var mediaPolicy = Guid.NewGuid();
        var providerPolicy = Guid.NewGuid();
        var agePolicy = Guid.NewGuid();

        var result = Evaluate(
            Summary(providers: ["youtube"], ageLimit: 18),
            Scope(mediaPolicy, mediaGuids: [MediaGuid]),
            Scope(providerPolicy, providers: ["youtube"]),
            Scope(agePolicy, minimumAges: [16]));

        result.IsAllowed.ShouldBeFalse();
        result.Decisions.Count(x => !x.Allowed).ShouldBe(3);
        result.Decisions.SelectMany(x => x.DenyingPolicyIds).ToHashSet()
            .ShouldBe(new HashSet<Guid> { mediaPolicy, providerPolicy, agePolicy });
    }

    [Test]
    public void Administrative_Bypass_Allows_Despite_Matching_Denies()
    {
        var result = AccessPolicyDenyEvaluator.Evaluate(
            Summary(providers: ["youtube"], ageLimit: 18),
            [
                Scope(
                    Guid.NewGuid(),
                    mediaGuids: [MediaGuid],
                    providers: ["youtube"],
                    minimumAges: [13])
            ],
            bypass: true);

        result.IsAllowed.ShouldBeTrue();
        result.Decisions.ShouldHaveSingleItem().Axis.ShouldBe("bypass");
    }

    [Test]
    public void Unrated_Media_Is_Not_Denied_By_Age_Tiers()
    {
        var result = Evaluate(
            Summary(ageLimit: null),
            Scope(Guid.NewGuid(), minimumAges: [0]));

        result.IsAllowed.ShouldBeTrue();
        var decision = result.Decisions.Single(x => x.Axis == "age");
        decision.Allowed.ShouldBeTrue();
        decision.Resource.ShouldBe("unrated");
        decision.DenyingPolicyIds.ShouldBeEmpty();
    }

    [Test]
    public void Any_Denied_Provider_Denies_Multi_Provider_Media()
    {
        var policyId = Guid.NewGuid();

        var result = Evaluate(
            Summary(providers: ["youtube", "vimeo"]),
            Scope(policyId, providers: ["vimeo"]));

        result.IsAllowed.ShouldBeFalse();
        result.Decisions.Single(x => x.Axis == "provider" && x.Resource == "youtube").Allowed.ShouldBeTrue();
        result.Decisions.Single(x => x.Axis == "provider" && x.Resource == "vimeo").Allowed.ShouldBeFalse();
    }

    [Test]
    public void Bundle_Only_Policy_Is_Valid_And_Empty_Policy_Is_Not()
    {
        var bundleOnly = new AccessPolicyDto
        {
            PolicyId = Guid.NewGuid(),
            Name = "Endpoint access",
            Enabled = true,
            BundleIds = ["watching"]
        };
        var empty = bundleOnly with { BundleIds = [] };

        AccessPolicyConsumerService.Validate(bundleOnly).ShouldBeNull();
        AccessPolicyConsumerService.Validate(empty).ShouldNotBeNull();
    }

    private static AccessPolicyEffectiveMediaDto Evaluate(
        AccessPolicyMediaSummaryDto summary,
        params AccessPolicyDenyScope[] scopes)
        => AccessPolicyDenyEvaluator.Evaluate(summary, scopes, bypass: false);

    private static AccessPolicyMediaSummaryDto Summary(
        IReadOnlyList<string>? providers = null,
        int? ageLimit = null)
        => new()
        {
            MediaGuid = MediaGuid,
            Found = true,
            Title = "Test media",
            Providers = providers ?? [],
            AgeLimit = ageLimit
        };

    private static AccessPolicyDenyScope Scope(
        Guid policyId,
        IReadOnlyCollection<Guid>? mediaGuids = null,
        IReadOnlyCollection<string>? providers = null,
        IReadOnlyCollection<int>? minimumAges = null)
        => new(
            policyId,
            mediaGuids ?? [],
            providers ?? [],
            minimumAges ?? []);
}
