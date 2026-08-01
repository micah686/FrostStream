using Conduit.NATS;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

public sealed class TopologySpecTests
{
    [Test]
    public async Task GetFilterSubjects_Prefers_List_When_Present()
    {
        var spec = new ConsumerSpec
        {
            FilterSubject = "single.subject",
            FilterSubjects = { "a.b", "c.d" }
        };

        spec.GetFilterSubjects().ShouldBe(new[] { "a.b", "c.d" });
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetFilterSubjects_Falls_Back_To_Single()
    {
        var spec = new ConsumerSpec { FilterSubject = "single.subject" };
        spec.GetFilterSubjects().ShouldBe(new[] { "single.subject" });
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetFilterSubjects_Empty_When_None_Set()
    {
        new ConsumerSpec().GetFilterSubjects().ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Spec_Defaults_Are_Sensible()
    {
        var stream = new StreamSpec();
        stream.RetentionPolicy.ShouldBe(StreamRetention.Limits);
        stream.StorageType.ShouldBe(StorageType.File);
        stream.MaxBytes.ShouldBe(-1);

        var consumer = new ConsumerSpec();
        consumer.AckPolicy.ShouldBe(AckPolicy.Explicit);
        consumer.DeliverPolicy.ShouldBe(DeliverPolicy.All);
        await Task.CompletedTask;
    }
}
