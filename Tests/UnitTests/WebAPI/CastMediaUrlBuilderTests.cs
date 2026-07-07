using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Media.Casting;

namespace UnitTests.WebAPI;

public sealed class CastMediaUrlBuilderTests
{
    private static readonly Guid MediaGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Test]
    public async Task Advertised_Base_Url_Wins_Over_Request_Host()
    {
        var builder = CreateBuilder("http://192.168.1.10:5041");
        var request = CreateRequest(host: "localhost:5041");

        var (baseUrl, error) = builder.ResolveBaseUrl(request);

        error.ShouldBeNull();
        baseUrl.ShouldBe("http://192.168.1.10:5041");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Origin_Header_Is_Used_When_No_Advertised_Base()
    {
        var builder = CreateBuilder(advertisedBaseUrl: null);
        var request = CreateRequest(host: "localhost:5041");
        request.Headers.Origin = "http://192.168.1.20:3000";

        var (baseUrl, error) = builder.ResolveBaseUrl(request);

        error.ShouldBeNull();
        baseUrl.ShouldBe("http://192.168.1.20:3000");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Referer_Origin_Is_Used_When_No_Origin_Header()
    {
        var builder = CreateBuilder(advertisedBaseUrl: null);
        var request = CreateRequest(host: "localhost:5041");
        request.Headers.Referer = "http://192.168.1.30:3000/watch/some-guid?x=1";

        var (baseUrl, error) = builder.ResolveBaseUrl(request);

        error.ShouldBeNull();
        baseUrl.ShouldBe("http://192.168.1.30:3000");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Request_Host_Is_The_Final_Fallback()
    {
        var builder = CreateBuilder(advertisedBaseUrl: null);
        var request = CreateRequest(host: "192.168.1.40:5041");

        var (baseUrl, error) = builder.ResolveBaseUrl(request);

        error.ShouldBeNull();
        baseUrl.ShouldBe("https://192.168.1.40:5041");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://localhost:5041")]
    [Arguments("http://127.0.0.1:5041")]
    [Arguments("https://[::1]:7035")]
    public async Task Loopback_Bases_Are_Rejected(string advertised)
    {
        var builder = CreateBuilder(advertised);
        var request = CreateRequest(host: "localhost:5041");

        var (baseUrl, error) = builder.ResolveBaseUrl(request);

        baseUrl.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("Cast:AdvertisedBaseUrl");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Loopback_Request_Host_Fallback_Is_Rejected()
    {
        var builder = CreateBuilder(advertisedBaseUrl: null);
        var request = CreateRequest(host: "localhost:5041");

        var (baseUrl, error) = builder.ResolveBaseUrl(request);

        baseUrl.ShouldBeNull();
        error.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Stream_Urls_Carry_The_Cast_Token()
    {
        var video = CastMediaUrlBuilder.BuildStreamUrl("http://10.0.0.2:5041", MediaGuid, "tok en", audio: false, format: null);
        var audio = CastMediaUrlBuilder.BuildStreamUrl("http://10.0.0.2:5041", MediaGuid, "abc", audio: true, format: "opus");

        video.ShouldBe($"http://10.0.0.2:5041/api/media/watch/{MediaGuid:D}?castToken=tok%20en");
        audio.ShouldBe($"http://10.0.0.2:5041/api/media/watch/{MediaGuid:D}?audio=true&format=opus&castToken=abc");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Caption_And_Thumbnail_Urls_Are_Well_Formed()
    {
        var caption = CastMediaUrlBuilder.BuildCaptionUrl("http://10.0.0.2:5041", MediaGuid, "en", "subtitles", "abc");
        var captionNoType = CastMediaUrlBuilder.BuildCaptionUrl("http://10.0.0.2:5041", MediaGuid, "en", null, "abc");
        var thumbnail = CastMediaUrlBuilder.BuildThumbnailUrl("http://10.0.0.2:5041", MediaGuid, "abc");

        caption.ShouldBe($"http://10.0.0.2:5041/api/media/watch/{MediaGuid:D}/captions/en?captionType=subtitles&castToken=abc");
        captionNoType.ShouldBe($"http://10.0.0.2:5041/api/media/watch/{MediaGuid:D}/captions/en?castToken=abc");
        thumbnail.ShouldBe($"http://10.0.0.2:5041/api/media/watch/{MediaGuid:D}/thumbnail?castToken=abc");
        await Task.CompletedTask;
    }

    private static CastMediaUrlBuilder CreateBuilder(string? advertisedBaseUrl)
        => new(Options.Create(new CastingOptions { AdvertisedBaseUrl = advertisedBaseUrl }));

    private static HttpRequest CreateRequest(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = HostString.FromUriComponent(host);
        return context.Request;
    }
}
