using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Worker;

public sealed class DeterministicGuidTests
{
    [Test]
    public void Create_Returns_Stable_Guid_For_Same_Seed_And_Suffix()
    {
        var seed = Guid.Parse("a37d8028-f616-489f-8562-249201dd289b");

        var first = DeterministicGuid.Create(seed, "metadata-fetched");
        var second = DeterministicGuid.Create(seed, "metadata-fetched");

        first.ShouldBe(second);
        first.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public void Create_Changes_When_Suffix_Changes()
    {
        var seed = Guid.Parse("a37d8028-f616-489f-8562-249201dd289b");

        DeterministicGuid.Create(seed, "downloaded")
            .ShouldNotBe(DeterministicGuid.Create(seed, "metadata-fetched"));
    }
}
