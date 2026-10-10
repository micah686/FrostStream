using Conduit.NATS;
using NATS.Client.Core;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

public sealed class MessageHeadersTests
{
    [Test]
    public async Task Empty_Has_No_Entries()
    {
        MessageHeaders.Empty.Headers.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Null_Dictionary_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new MessageHeaders(null!));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Whitespace_Key_Throws()
    {
        Should.Throw<ArgumentException>(() => new MessageHeaders(new Dictionary<string, string> { [" "] = "v" }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Null_Value_Throws()
    {
        Should.Throw<ArgumentException>(() => new MessageHeaders(new Dictionary<string, string> { ["k"] = null! }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Converter_ToNats_Returns_Null_For_Empty_Or_Null()
    {
        NatsHeaderConverter.ToNats(null).ShouldBeNull();
        NatsHeaderConverter.ToNats(MessageHeaders.Empty).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Converter_RoundTrips_Headers()
    {
        var original = new MessageHeaders(new Dictionary<string, string> { ["x-tenant"] = "acme", ["x-trace"] = "abc" });

        var nats = NatsHeaderConverter.ToNats(original);
        nats.ShouldNotBeNull();

        var back = NatsHeaderConverter.FromNats(nats);
        back.Headers["x-tenant"].ShouldBe("acme");
        back.Headers["x-trace"].ShouldBe("abc");

        NatsHeaderConverter.FromNats((NatsHeaders?)null).Headers.ShouldBeEmpty();
        await Task.CompletedTask;
    }
}
