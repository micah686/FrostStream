using System.Security.Claims;
using IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Auth;
using Shared.Secrets;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Cookies.Controllers;
using WebAPI.Features.Cookies.Models;

namespace IntegrationTests.Cookies;

/// <summary>
/// End-to-end verification of the cookie-profile feature against real Postgres (migration 027),
/// real NATS (the cookie-profile request/reply flow), and real OpenBAO (user-scoped read/write).
/// Drives the production <see cref="CookiesController"/> with its real dependencies — only the
/// authenticated subject is faked.
/// </summary>
public class CookieProfileFlowTests
{
    private static readonly CookieProfileStackFixture Fixture = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    static CookieProfileFlowTests()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Before(Test)]
    public async Task SetupAsync()
    {
        await Gate.WaitAsync();
        await Fixture.InitializeAsync();
    }

    [After(Test)]
    public void Release() => Gate.Release();

    [Test]
    public async Task Migration_027_Creates_Cookie_Profiles_Table()
    {
        (await Fixture.CookieProfilesTableExistsAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task Upsert_Writes_Secret_To_OpenBao_And_Metadata_To_Postgres()
    {
        const string subject = "user-alice";
        var store = Fixture.CreateSecretStore();
        var controller = CreateController(store, subject);

        var result = await controller.Upsert("youtube", new CookieUpsertRequest
        {
            Content = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc",
            Site = "youtube.com",
            DisplayName = "My YouTube"
        }, CancellationToken.None);

        // Returns metadata only.
        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CookieProfileResponse>();
        dto.ProfileKey.ShouldBe("youtube");
        dto.Site.ShouldBe("youtube.com");

        // Secret body lands in OpenBAO at the user-scoped path.
        var secret = await store.ReadAsync("cookies/users/user-alice/youtube");
        secret.ShouldNotBeNull();
        secret!["content"].ShouldContain("Netscape HTTP Cookie File");

        // Only non-secret metadata is persisted in Postgres.
        var row = await Fixture.FindProfileAsync(subject, "youtube");
        row.ShouldNotBeNull();
        row!.Site.ShouldBe("youtube.com");
        row.DisplayName.ShouldBe("My YouTube");
    }

    [Test]
    public async Task List_And_Get_Return_Metadata_Without_Body()
    {
        const string subject = "user-list";
        var store = Fixture.CreateSecretStore();
        var controller = CreateController(store, subject);
        await controller.Upsert("twitch", new CookieUpsertRequest { Content = "cookie", Site = "twitch.tv" }, CancellationToken.None);

        var list = (await controller.List(CancellationToken.None)).Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeAssignableTo<IReadOnlyCollection<CookieProfileResponse>>();
        list!.ShouldContain(x => x.ProfileKey == "twitch");

        var got = (await controller.Get("twitch", CancellationToken.None)).Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CookieProfileResponse>();
        got.Site.ShouldBe("twitch.tv");
        // The response type structurally cannot carry the cookie body.
    }

    [Test]
    public async Task Delete_Removes_Both_Secret_And_Metadata()
    {
        const string subject = "user-del";
        var store = Fixture.CreateSecretStore();
        var controller = CreateController(store, subject);
        await controller.Upsert("vimeo", new CookieUpsertRequest { Content = "cookie" }, CancellationToken.None);

        var result = await controller.Delete("vimeo", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.ReadAsync("cookies/users/user-del/vimeo")).ShouldBeNull();
        (await Fixture.FindProfileAsync(subject, "vimeo")).ShouldBeNull();
    }

    [Test]
    public async Task Profiles_Are_Isolated_Between_Users()
    {
        var store = Fixture.CreateSecretStore();
        await CreateController(store, "user-a").Upsert("shared", new CookieUpsertRequest { Content = "a-secret" }, CancellationToken.None);
        await CreateController(store, "user-b").Upsert("shared", new CookieUpsertRequest { Content = "b-secret" }, CancellationToken.None);

        // user-b cannot see user-a's profile, even with the same profile key.
        var bobList = (await CreateController(store, "user-b").List(CancellationToken.None)).Result
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IReadOnlyCollection<CookieProfileResponse>>();
        bobList!.Count(x => x.ProfileKey == "shared").ShouldBe(1);

        // Secrets live at distinct, isolated OpenBAO paths.
        (await store.ReadAsync("cookies/users/user-a/shared"))!["content"].ShouldBe("a-secret");
        (await store.ReadAsync("cookies/users/user-b/shared"))!["content"].ShouldBe("b-secret");
    }

    private static CookiesController CreateController(ISecretStore store, string subject)
    {
        var controller = new CookiesController(store, Fixture.Bus, NullLogger<CookiesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(AuthConstants.SubjectClaim, subject) }, "Test"))
                }
            }
        };

        return controller;
    }
}
