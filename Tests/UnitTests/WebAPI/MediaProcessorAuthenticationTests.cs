using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Auth;
using Shouldly;
using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI;

public sealed class MediaProcessorAuthenticationTests
{
    [Test]
    public async Task Authentication_Accepts_The_Configured_Api_Key()
    {
        var result = await AuthenticateAsync("configured-key", "configured-key");

        result.Succeeded.ShouldBeTrue();
        AuthConstants.FindSubject(result.Principal).ShouldBe("mediaprocessor");
    }

    [Test]
    [Arguments(null, "configured-key")]
    [Arguments("wrong-key", "configured-key")]
    [Arguments("configured-key", null)]
    public async Task Authentication_Fails_Closed(string? provided, string? configured)
    {
        var result = await AuthenticateAsync(provided, configured);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(string? provided, string? configured)
    {
        var schemeOptions = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemeOptions.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new MediaProcessorAuthenticationHandler(
            schemeOptions,
            Options.Create(new MediaProcessorAuthOptions { ApiKey = configured }),
            new LoggerFactory(),
            UrlEncoder.Default);
        var context = new DefaultHttpContext();
        if (provided is not null)
        {
            context.Request.Headers[MediaProcessorAuthenticationDefaults.ApiKeyHeader] = provided;
        }

        await handler.InitializeAsync(
            new AuthenticationScheme(
                MediaProcessorAuthenticationDefaults.Scheme,
                null,
                typeof(MediaProcessorAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }
}
