using System.Net;
using System.Security.Claims;
using FrostStream.ApplicationContracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shared.Auth;
using Shared.Deployment;
using Shouldly;
using WebAPI;
using WebAPI.Auth;
using WebAPI.Features.Media;

namespace UnitTests.WebAPI.Auth;

public sealed class LiteBackendAccessTests
{
    [Test]
    public async Task Lite_All_Routes_Use_Admin_Despite_Credentials_Roles_And_Explicit_Schemes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration["Deployment:Mode"] = "Lite";
        builder.Configuration["Auth:SingleUserMode"] = "false";
        builder.Configuration["Auth:Authority"] = "https://invalid.example";
        builder.Configuration["Auth:AllowSingleUserModeInProduction"] = "false";
        builder.AddWebAPIModule();
        builder.Services.RemoveAll<IHostedService>();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseAuthentication();
        app.UseAuthorization();
        static string Identity(HttpContext context) => $"{AuthConstants.FindSubject(context.User)}:{context.User.Identity!.Name}";
        app.MapGet("/fallback", Identity);
        app.MapGet("/default", Identity).RequireAuthorization();
        app.MapGet("/permission", Identity).RequireAuthorization(EndpointPolicy.PolicyName("future-endpoint"));
        app.MapGet("/role", Identity).RequireAuthorization(new AuthorizeAttribute { Roles = "unlisted-role", AuthenticationSchemes = "unregistered-external-scheme" });
        app.MapGet("/anonymous", Identity).AllowAnonymous();
        app.MapPost("/write", Identity);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "old-invalid-token");
        client.DefaultRequestHeaders.Add("Cookie", $"{BffAuthenticationDefaults.CookieName}=old-cookie");
        foreach (var route in new[] { "fallback", "default", "permission", "role", "anonymous" })
        {
            using var response = await client.GetAsync($"/{route}?cast_token=invalid&podcast_token=invalid");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldBe($"{AuthConstants.SingleUserSubject}:Admin");
        }
        using var write = await client.PostAsync("/write", new StringContent("{}"));
        write.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMediaAccessChecker>().ShouldBeOfType<LiteMediaAccessChecker>();
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        (await authorization.AuthorizeAsync(new ClaimsPrincipal(), null, new[] { new DenyAnonymousAuthorizationRequirement() })).Succeeded.ShouldBeTrue();
        (await authorization.AuthorizeAsync(new ClaimsPrincipal(), null, "future-policy")).Succeeded.ShouldBeTrue();
        await app.StopAsync();
    }

    [Test]
    public async Task Lite_Media_Access_Is_Independent_Of_Tokens_And_Configured_Admin_Groups()
    {
        var checker = new LiteMediaAccessChecker();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(CastTokenClaims.MediaGuid, Guid.NewGuid().ToString())]));
        (await checker.CheckWatchAccessAsync(user, Guid.NewGuid(), default)).ShouldBeNull();
    }

    [Test]
    public async Task Full_Retains_Fallback_Role_And_Resource_Protection()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration["Auth:SingleUserMode"] = "false";
        builder.Configuration["Auth:Authority"] = "https://identity.example";
        builder.Configuration["Auth:PublicOrigin"] = "http://localhost";
        builder.Configuration["Auth:ClientSecret"] = "test-secret";
        builder.AddWebAPIModule();
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        provider.ShouldBeOfType<EndpointPolicyProvider>();
        scope.ServiceProvider.GetRequiredService<IFrostStreamAuthorizer>().ShouldBeOfType<OpenFgaAuthorizer>();
        scope.ServiceProvider.GetRequiredService<IMediaAccessChecker>().ShouldBeOfType<MediaAccessChecker>();
        var schemes = await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        schemes.ShouldContain(s => s.Name == Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        schemes.ShouldContain(s => s.Name == BffAuthenticationDefaults.CookieScheme);
        schemes.ShouldNotContain(s => s.Name == AuthConstants.SingleUserScheme);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admins")], "test"));
        (await authorization.AuthorizeAsync(user, null, (await provider.GetFallbackPolicyAsync())!.Requirements)).Succeeded.ShouldBeFalse();
        (await authorization.AuthorizeAsync(user, null, new[] { new RolesAuthorizationRequirement(["unlisted-role"]) })).Succeeded.ShouldBeFalse();
        var checker = new MediaAccessChecker(Substitute.For<IMessageBus>(), NullLogger<MediaAccessChecker>.Instance);
        var scopedUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim(CastTokenClaims.MediaGuid, Guid.NewGuid().ToString())]));
        (await checker.CheckWatchAccessAsync(scopedUser, Guid.NewGuid(), default)).ShouldBeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().StatusCode.ShouldBe(403);
    }
}
