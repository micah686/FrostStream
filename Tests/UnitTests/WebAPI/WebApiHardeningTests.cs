using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI;

public sealed class WebApiHardeningTests
{
    [Test]
    public void Production_Single_User_Mode_Fails_By_Default()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            WebApiHardening.ValidateStartup(
                new FrostStreamAuthOptions(),
                singleUserMode: true,
                isProduction: true));

        exception.Message.ShouldContain("SINGLE_USER_MODE");
    }

    [Test]
    public void Production_Single_User_Mode_Can_Be_Explicitly_Allowed()
    {
        Should.NotThrow(() =>
            WebApiHardening.ValidateStartup(
                new FrostStreamAuthOptions { AllowSingleUserModeInProduction = true },
                singleUserMode: true,
                isProduction: true));
    }

    [Test]
    public void Development_Single_User_Mode_Remains_Allowed()
    {
        Should.NotThrow(() =>
            WebApiHardening.ValidateStartup(
                new FrostStreamAuthOptions(),
                singleUserMode: true,
                isProduction: false));
    }
}
