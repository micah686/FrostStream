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

    [Test]
    public void Multi_User_Mode_Requires_Bff_Settings()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            WebApiHardening.ValidateStartup(
                new FrostStreamAuthOptions { Authority = "https://auth.example.test" },
                singleUserMode: false,
                isProduction: false));

        exception.Message.ShouldContain("Auth:PublicOrigin");
    }

    [Test]
    public void Multi_User_Development_Settings_Are_Accepted()
    {
        Should.NotThrow(() =>
            WebApiHardening.ValidateStartup(
                ValidMultiUserOptions(),
                singleUserMode: false,
                isProduction: false));
    }

    [Test]
    public void Multi_User_Mode_Rejects_Invalid_Public_Authority()
    {
        var options = new FrostStreamAuthOptions
        {
            Authority = "http://authentik:9000/application/o/froststream/",
            PublicAuthority = "not-a-uri",
            PublicOrigin = "http://localhost:25000",
            ClientId = "froststream-bff",
            ClientSecret = "test-secret",
            Scopes = "openid profile email groups offline_access"
        };

        var exception = Should.Throw<InvalidOperationException>(() =>
            WebApiHardening.ValidateStartup(options, singleUserMode: false, isProduction: false));

        exception.Message.ShouldContain("Auth:PublicAuthority");
    }

    [Test]
    public void Production_Multi_User_Mode_Requires_Secure_Cookies()
    {
        var options = new FrostStreamAuthOptions
        {
            Authority = "https://auth.example.test/application/o/froststream/",
            PublicAuthority = "https://auth.example.test/application/o/froststream/",
            PublicOrigin = "https://froststream.example.test",
            ClientId = "froststream-bff",
            ClientSecret = "test-secret",
            Scopes = "openid profile email groups offline_access",
            DataProtectionKeysPath = "/keys"
        };

        var exception = Should.Throw<InvalidOperationException>(() =>
            WebApiHardening.ValidateStartup(options, singleUserMode: false, isProduction: true));

        exception.Message.ShouldContain("Auth:SecureCookies");
    }

    [Test]
    [Arguments("/library", "/library")]
    [Arguments("/watch/abc?from=home", "/watch/abc?from=home")]
    [Arguments("//evil.example/path", "/profile")]
    [Arguments("https://evil.example/path", "/profile")]
    [Arguments("/ok\r\nLocation: https://evil.example", "/profile")]
    public void Return_Path_Is_Restricted_To_A_Local_Absolute_Path(string value, string expected)
    {
        LocalReturnPath.Normalize(value).ShouldBe(expected);
    }

    private static FrostStreamAuthOptions ValidMultiUserOptions() => new()
    {
        Authority = "http://authentik:9000/application/o/froststream/",
        PublicOrigin = "http://localhost:25000",
        ClientId = "froststream-bff",
        ClientSecret = "test-secret",
        Scopes = "openid profile email groups offline_access"
    };
}
