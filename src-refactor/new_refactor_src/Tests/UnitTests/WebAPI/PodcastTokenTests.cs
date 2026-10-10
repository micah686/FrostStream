using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Auth;
using Shouldly;
using System.Security.Claims;
using System.Text.Encodings.Web;
using TUnit.Core;
using WebAPI.Features.Media;

namespace UnitTests.WebAPI;

public sealed class PodcastTokenTests
{
    [Test]
    public async Task Token_Round_Trips_The_Issuing_Identity_And_Channel()
    {
        var service = CreateService();
        var principal = CreatePrincipal();

        var (token, expiresAt) = service.Issue(principal, 42);
        var payload = service.Validate(token);

        payload.ShouldNotBeNull();
        payload.Subject.ShouldBe("user-123");
        payload.AccountId.ShouldBe(42);
        payload.Groups.ShouldBe(["listeners"]);
        expiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(3000));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Authentication_Rejects_A_Token_On_Another_Channel_Route()
    {
        var service = CreateService();
        var (token, _) = service.Issue(CreatePrincipal(), 42);
        var handler = CreateHandler(service);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/media/channels/99/audio/podcast.rss";
        context.Request.QueryString = QueryString.Create(PodcastTokenDefaults.QueryParameter, token);
        context.Request.RouteValues["accountId"] = 99L;
        await handler.InitializeAsync(
            new AuthenticationScheme(PodcastTokenDefaults.Scheme, null, typeof(PodcastTokenAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Test]
    public async Task Authentication_Accepts_The_Matching_Channel_Route()
    {
        var service = CreateService();
        var (token, _) = service.Issue(CreatePrincipal(), 42);
        var handler = CreateHandler(service);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/media/channels/42/audio/podcast.rss";
        context.Request.QueryString = QueryString.Create(PodcastTokenDefaults.QueryParameter, token);
        context.Request.RouteValues["accountId"] = 42L;
        await handler.InitializeAsync(
            new AuthenticationScheme(PodcastTokenDefaults.Scheme, null, typeof(PodcastTokenAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        AuthConstants.FindSubject(result.Principal).ShouldBe("user-123");
    }

    private static PodcastTokenService CreateService()
        => new(
            Options.Create(new PodcastTokenOptions()),
            new EphemeralDataProtectionProvider());

    private static PodcastTokenAuthenticationHandler CreateHandler(PodcastTokenService service)
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        return new PodcastTokenAuthenticationHandler(options, new LoggerFactory(), UrlEncoder.Default, service);
    }

    private static ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
        [
            new Claim(AuthConstants.SubjectClaim, "user-123"),
            new Claim(AuthConstants.PreferredUsernameClaim, "listener"),
            new Claim(AuthConstants.GroupsClaim, "listeners")
        ],
        "test"));
}
