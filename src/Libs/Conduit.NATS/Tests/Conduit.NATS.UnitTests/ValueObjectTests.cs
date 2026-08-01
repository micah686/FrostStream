using Conduit.NATS;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

public sealed class ValueObjectTests
{
    [Test]
    [Arguments("FROSTSTREAM_DOWNLOAD")]
    [Arguments("a-b_c123")]
    public async Task StreamName_Accepts_Valid(string value)
    {
        StreamName.From(value).Value.ShouldBe(value);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("")]
    [Arguments("has space")]
    [Arguments("has.dot")]
    [Arguments("wild*card")]
    public async Task StreamName_Rejects_Invalid(string value)
    {
        Should.Throw<Exception>(() => StreamName.From(value));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("download.requested")]
    [Arguments("download.>")]
    [Arguments("download.*.done")]
    public async Task SubjectName_Accepts_Valid_Including_Wildcards(string value)
    {
        SubjectName.From(value).Value.ShouldBe(value);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("download..requested")]
    [Arguments("download.>.tail")]
    [Arguments("")]
    public async Task SubjectName_Rejects_Invalid(string value)
    {
        Should.Throw<Exception>(() => SubjectName.From(value));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConsumerAndBucketAndQueue_Reject_Dots()
    {
        Should.Throw<Exception>(() => ConsumerName.From("a.b"));
        Should.Throw<Exception>(() => BucketName.From("a.b"));
        Should.Throw<Exception>(() => QueueGroup.From("a b"));
        await Task.CompletedTask;
    }
}
