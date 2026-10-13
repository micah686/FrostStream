using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Worker;

public sealed class ReturnYouTubeDislikeClientTests
{
    [Test]
    public async Task GetVotesAsync_Returns_Null_And_Does_Not_Request_When_Disabled()
    {
        var handler = new StubHandler(_ => Json("""{"dislikes":12}"""));
        var sut = CreateSut(handler, enabled: false);

        var result = await sut.GetVotesAsync("video-1", CancellationToken.None);

        result.ShouldBeNull();
        handler.RequestCount.ShouldBe(0);
    }

    [Test]
    public async Task GetVotesAsync_Parses_Success_Response()
    {
        var handler = new StubHandler(request =>
        {
            request.RequestUri!.PathAndQuery.ShouldBe("/votes?videoId=video-1");
            return Json("""{"id":"video-1","likes":100,"dislikes":12,"rating":4.25,"viewCount":1000}""");
        });
        var sut = CreateSut(handler, enabled: true);

        var result = await sut.GetVotesAsync("video-1", CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe("video-1");
        result.Likes.ShouldBe(100);
        result.Dislikes.ShouldBe(12);
        result.Rating.ShouldBe(4.25);
        result.ViewCount.ShouldBe(1000);
    }

    [Test]
    public async Task GetVotesAsync_Returns_Null_On_Rate_Limit()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var sut = CreateSut(handler, enabled: true);

        var result = await sut.GetVotesAsync("video-1", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetVotesAsync_Returns_Null_On_Invalid_Json()
    {
        var handler = new StubHandler(_ => Json("""{"dislikes":""not-a-number""}"""));
        var sut = CreateSut(handler, enabled: true);

        var result = await sut.GetVotesAsync("video-1", CancellationToken.None);

        result.ShouldBeNull();
    }

    private static ReturnYouTubeDislikeClient CreateSut(StubHandler handler, bool enabled)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://returnyoutubedislikeapi.test/")
        };

        return new ReturnYouTubeDislikeClient(
            httpClient,
            Options.Create(new WorkerOptions
            {
                ReturnYouTubeDislike = new ReturnYouTubeDislikeOptions
                {
                    Enabled = enabled,
                    BaseUrl = new Uri("https://returnyoutubedislikeapi.test/"),
                    Timeout = TimeSpan.FromSeconds(5)
                }
            }),
            NullLogger<ReturnYouTubeDislikeClient>.Instance);
    }

    private static HttpResponseMessage Json(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}
