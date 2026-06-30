using DataBridge.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class MediaAccessExecutorTests
{
    [Test]
    public void Intersects_Is_True_When_A_Group_Matches()
    {
        var userGroups = new HashSet<string>(["staff", "adults"], StringComparer.OrdinalIgnoreCase);

        MediaAccessExecutor.Intersects(["adults"], userGroups).ShouldBeTrue();
    }

    [Test]
    public void Intersects_Is_Case_Insensitive()
    {
        var userGroups = new HashSet<string>(["Adults"], StringComparer.OrdinalIgnoreCase);

        MediaAccessExecutor.Intersects(["adults"], userGroups).ShouldBeTrue();
    }

    [Test]
    public void Intersects_Is_False_When_No_Group_Matches()
    {
        var userGroups = new HashSet<string>(["staff"], StringComparer.OrdinalIgnoreCase);

        MediaAccessExecutor.Intersects(["adults", "vip"], userGroups).ShouldBeFalse();
    }

    [Test]
    public void Intersects_Is_False_For_Empty_Allow_List()
    {
        var userGroups = new HashSet<string>(["staff"], StringComparer.OrdinalIgnoreCase);

        MediaAccessExecutor.Intersects([], userGroups).ShouldBeFalse();
    }

    [Test]
    public void SelectHighestApplicableTier_Picks_The_Highest_Tier_At_Or_Below_The_Age_Limit()
    {
        MediaAccessExecutor.SelectHighestApplicableTier([13, 17, 18], ageLimit: 18).ShouldBe(18);
        MediaAccessExecutor.SelectHighestApplicableTier([13, 17, 18], ageLimit: 17).ShouldBe(17);
        MediaAccessExecutor.SelectHighestApplicableTier([13, 17, 18], ageLimit: 16).ShouldBe(13);
    }

    [Test]
    public void SelectHighestApplicableTier_Returns_Null_When_No_Tier_Applies()
    {
        MediaAccessExecutor.SelectHighestApplicableTier([17, 18], ageLimit: 0).ShouldBeNull();
        MediaAccessExecutor.SelectHighestApplicableTier([], ageLimit: 18).ShouldBeNull();
    }

    [Test]
    public void SelectHighestApplicableTier_Includes_A_Tier_That_Equals_The_Age_Limit()
    {
        MediaAccessExecutor.SelectHighestApplicableTier([18], ageLimit: 18).ShouldBe(18);
    }
}
