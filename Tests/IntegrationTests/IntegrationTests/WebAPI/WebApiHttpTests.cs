using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodaTime;
using NSubstitute;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Downloads.Models;
using YtDlpSharpLib.Options;

namespace IntegrationTests.WebApiHttp;

public sealed class WebApiHttpTests
{
    [Test]
    public async Task Get_Media_Content_Supports_Http_Range_Requests()
    {
        var mediaGuid = Guid.NewGuid();
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        using var factory = new TestWebApiFactory();
        factory.MessageBus.MediaContentResponse = new MediaContentResolveResponseMessage
        {
            Success = true,
            Item = new MediaContentLocationDto
            {
                MediaGuid = mediaGuid,
                StorageKey = "storage-a",
                StoragePath = "media/video.mp4",
                Version = 3
            }
        };
        factory.BlobStorageProvider.GetAsync("storage-a", Arg.Any<CancellationToken>())
            .Returns(factory.BlobStorage);
        factory.BlobStorage.OpenReadAsync("media/video.mp4", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/media/{mediaGuid}/content?storageKey=storage-a");
        request.Headers.Range = new RangeHeaderValue(2, 4);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
        response.Content.Headers.ContentRange!.From.ShouldBe(2);
        response.Content.Headers.ContentRange.To.ShouldBe(4);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe([2, 3, 4]);
        factory.MessageBus.MediaContentRequest.ShouldNotBeNull();
        factory.MessageBus.MediaContentRequest.StorageKey.ShouldBe("storage-a");
        factory.MessageBus.MediaContentRequest.Version.ShouldBeNull();
    }

    [Test]
    public async Task Post_Download_Audio_Publishes_Message_Through_Http_Surface()
    {
        using var factory = new TestWebApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/downloads/audio", new
        {
            sourceUrl = "https://example.test/audio",
            storageKey = "",
            requestedBy = "micah",
            cookieKey = "member-cookie"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<DownloadRequestResponse>();
        body.ShouldNotBeNull();
        body.JobId.ShouldNotBe(Guid.Empty);

        var published = factory.Publisher.Single<DownloadRequested>();
        published.Subject.ShouldBe(DownloadSubjects.DownloadRequested);
        published.MessageId.ShouldNotBeNullOrWhiteSpace();
        published.Message.JobId.ShouldBe(body.JobId);
        published.Message.SourceUrl.ShouldBe("https://example.test/audio");
        published.Message.StorageKey.ShouldBe("default");
        published.Message.RequestedBy.ShouldBe("micah");
        published.Message.CookieKey.ShouldBe("member-cookie");
        published.Message.MediaKind.ShouldBe(MediaKind.Audio);
        published.Message.AudioFormat.ShouldBe(AudioConversionFormat.Mp3);
        published.Message.OccurredAt.ShouldBe(TestWebApiFactory.Now);
    }

    [Test]
    public async Task Post_Schedule_With_Invalid_Trigger_Returns_Model_Validation_400()
    {
        using var factory = new TestWebApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/schedules", new
        {
            key = "daily-refresh",
            taskType = "channel_asset_refresh",
            cron = "0 0 3 ? * *",
            intervalSeconds = 60,
            timezone = "UTC"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Put_Cookie_Writes_To_Fake_Secret_Store_Through_Http_Surface()
    {
        using var factory = new TestWebApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/cookies/member-cookie", new
        {
            content = "# Netscape HTTP Cookie File"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.SecretStore.Secrets["cookies/member-cookie"]["content"]
            .ShouldBe("# Netscape HTTP Cookie File");
    }
}

internal sealed class TestWebApiFactory : WebApplicationFactory<global::WebAPI.Program>
{
    public static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 15, 0);

    public CapturingJetStreamPublisher Publisher { get; } = new();
    public FakeMessageBus MessageBus { get; } = new();
    public InMemorySecretStore SecretStore { get; } = new();
    public IBlobStorageProvider BlobStorageProvider { get; } = Substitute.For<IBlobStorageProvider>();
    public IBlobStorage BlobStorage { get; } = Substitute.For<IBlobStorage>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IJetStreamPublisher>();
            services.RemoveAll<IMessageBus>();
            services.RemoveAll<ISecretStore>();
            services.RemoveAll<IClock>();
            services.RemoveAll<IBlobStorageProvider>();

            services.AddSingleton<IJetStreamPublisher>(Publisher);
            services.AddSingleton<IMessageBus>(MessageBus);
            services.AddSingleton<ISecretStore>(SecretStore);
            services.AddSingleton<IClock>(new TestClock(Now));
            services.AddSingleton(BlobStorageProvider);
        });
    }
}

internal sealed class TestClock(Instant now) : IClock
{
    public Instant GetCurrentInstant() => now;
}

internal sealed class CapturingJetStreamPublisher : IJetStreamPublisher
{
    private readonly List<object> _published = [];

    public Task PublishAsync<T>(
        string subject,
        T message,
        string? messageId,
        MessageHeaders? headers = null,
        CancellationToken cancellationToken = default)
    {
        _published.Add(new Published<T>(subject, message, messageId, headers));
        return Task.CompletedTask;
    }

    public Task PublishBatchAsync<T>(
        IReadOnlyList<BatchMessage<T>> messages,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Published<T> Single<T>()
        => _published.ShouldHaveSingleItem().ShouldBeOfType<Published<T>>();
}

internal sealed record Published<T>(string Subject, T Message, string? MessageId, MessageHeaders? Headers);

internal sealed class FakeMessageBus : IMessageBus
{
    public MediaContentResolveResponseMessage? MediaContentResponse { get; set; }
    public MediaContentResolveRequestMessage? MediaContentRequest { get; private set; }

    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PublishAsync<T>(
        string subject,
        T message,
        MessageHeaders? headers,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync<T>(
        string subject,
        Func<IMessageContext<T>, Task> handler,
        string? queueGroup = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ISubscription>(new NoopSubscription());

    public Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (subject == MediaContentSubjects.Resolve &&
            request is MediaContentResolveRequestMessage mediaContentRequest &&
            MediaContentResponse is not null)
        {
            MediaContentRequest = mediaContentRequest;
            return Task.FromResult((TResponse?)(object)MediaContentResponse);
        }

        return Task.FromResult<TResponse?>(default);
    }
}

internal sealed class NoopSubscription : ISubscription
{
    public Guid Id { get; } = Guid.NewGuid();

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<string, IReadOnlyDictionary<string, string>> Secrets { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, string>?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Secrets.GetValueOrDefault(path));

    public Task WriteAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        Secrets[path] = new Dictionary<string, string>(values, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        Secrets.Remove(path);
        return Task.CompletedTask;
    }
}
