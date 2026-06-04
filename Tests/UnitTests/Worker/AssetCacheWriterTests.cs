using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Worker.Services;

namespace UnitTests.Worker;

public sealed class AssetCacheWriterTests
{
    [Test]
    public async Task DownloadAndStoreAsync_Stores_Content_In_Kind_And_Hash_Sharded_Path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"froststream-assets-{Guid.NewGuid():N}");
        var handler = new QueueingHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "avatar-bytes"u8.ToArray(), "image/png");
        var sut = CreateSut(root, handler);

        try
        {
            var result = await sut.DownloadAndStoreAsync(
                "https://example.test/channel/avatar",
                AssetKind.Avatar,
                CancellationToken.None);

            result.Extension.ShouldBe(".png");
            result.ContentLength.ShouldBe("avatar-bytes"u8.ToArray().Length);
            result.Attempts.ShouldBe(1);
            result.ReusedExisting.ShouldBeFalse();
            result.CachePath.ShouldStartWith(Path.Combine(root, "avatars"));
            result.CachePath.ShouldEndWith(".png");
            File.Exists(result.CachePath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(result.CachePath)).ShouldBe("avatar-bytes"u8.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task DownloadAndStoreAsync_Reuses_Existing_File_For_Same_Content()
    {
        var root = Path.Combine(Path.GetTempPath(), $"froststream-assets-{Guid.NewGuid():N}");
        var handler = new QueueingHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "same-bytes"u8.ToArray(), "image/jpeg");
        handler.Enqueue(HttpStatusCode.OK, "same-bytes"u8.ToArray(), "image/jpeg");
        var sut = CreateSut(root, handler);

        try
        {
            var first = await sut.DownloadAndStoreAsync(
                "https://example.test/banner.jpg",
                AssetKind.Banner,
                CancellationToken.None);
            var second = await sut.DownloadAndStoreAsync(
                "https://example.test/other-path.jpg",
                AssetKind.Banner,
                CancellationToken.None);

            second.CachePath.ShouldBe(first.CachePath);
            second.ContentHash.ShouldBe(first.ContentHash);
            second.ReusedExisting.ShouldBeTrue();
            Directory.GetFiles(Path.Combine(root, "banners"), "*", SearchOption.AllDirectories)
                .Single()
                .ShouldBe(first.CachePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task DownloadAndStoreAsync_Retries_Transient_Http_Failure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"froststream-assets-{Guid.NewGuid():N}");
        var handler = new QueueingHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, [] , "text/plain");
        handler.Enqueue(HttpStatusCode.OK, "banner-bytes"u8.ToArray(), "image/webp");
        var sut = CreateSut(root, handler);

        try
        {
            var result = await sut.DownloadAndStoreAsync(
                "https://example.test/banner",
                AssetKind.Banner,
                CancellationToken.None);

            result.Attempts.ShouldBe(2);
            result.Extension.ShouldBe(".webp");
            handler.RequestCount.ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AssetCacheWriter CreateSut(string root, QueueingHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("asset-cache").Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new AssetCacheWriter(
            factory,
            Options.Create(new AssetCacheOptions
            {
                Root = root,
                MaxAttempts = 2,
                InitialBackoff = TimeSpan.FromMilliseconds(1)
            }),
            NullLogger<AssetCacheWriter>.Instance);
    }

    private sealed class QueueingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public int RequestCount { get; private set; }

        public void Enqueue(HttpStatusCode statusCode, byte[] content, string contentType)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            _responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
