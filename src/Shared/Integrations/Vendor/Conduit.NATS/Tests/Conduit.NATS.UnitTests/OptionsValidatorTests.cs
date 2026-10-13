using Conduit.NATS;
using Shouldly;
using TUnit.Core;

namespace Conduit.NATS.UnitTests;

public sealed class OptionsValidatorTests
{
    private static readonly ConduitNatsOptionsValidator Validator = new();

    [Test]
    public async Task Valid_Options_Succeed()
    {
        var result = Validator.Validate(null, new ConduitNatsOptions { Url = "nats://localhost:4222" });
        result.Succeeded.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Empty_Url_Fails(string url)
    {
        Validator.Validate(null, new ConduitNatsOptions { Url = url }).Failed.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NonPositive_RequestTimeout_Fails()
    {
        Validator.Validate(null, new ConduitNatsOptions { RequestTimeout = TimeSpan.Zero }).Failed.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MaxReconnect_Below_NegativeOne_Fails()
    {
        Validator.Validate(null, new ConduitNatsOptions { MaxReconnect = -2 }).Failed.ShouldBeTrue();
        Validator.Validate(null, new ConduitNatsOptions { MaxReconnect = -1 }).Succeeded.ShouldBeTrue();
        await Task.CompletedTask;
    }
}
