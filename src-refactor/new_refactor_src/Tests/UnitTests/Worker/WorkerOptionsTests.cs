using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Worker;

public sealed class WorkerOptionsTests
{
    [Test]
    public void EffectiveYtDlpMinDelay_Defaults_To_Three_Seconds_When_Unset()
        => new WorkerOptions().EffectiveYtDlpMinDelay().ShouldBe(TimeSpan.FromSeconds(3));

    [Test]
    public void EffectiveYtDlpMinDelay_Clamps_Values_Below_The_Floor()
        => new WorkerOptions { YtDlpMinDelayBetweenStarts = TimeSpan.FromSeconds(1) }
            .EffectiveYtDlpMinDelay().ShouldBe(TimeSpan.FromSeconds(3));

    [Test]
    public void EffectiveYtDlpMinDelay_Keeps_Values_Above_The_Floor()
        => new WorkerOptions { YtDlpMinDelayBetweenStarts = TimeSpan.FromSeconds(10) }
            .EffectiveYtDlpMinDelay().ShouldBe(TimeSpan.FromSeconds(10));
}
