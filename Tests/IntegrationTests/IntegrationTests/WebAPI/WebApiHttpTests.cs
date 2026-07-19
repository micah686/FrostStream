using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentStorage.Blobs;
using Conduit.NATS;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodaTime;
using NSubstitute;
using Shared.Auth;
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
    public async Task Get_Media_Stream_Supports_Http_Range_Requests()
    {
        var mediaGuid = Guid.NewGuid();
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        using var factory = new TestWebApiFactory();
        factory.MessageBus.MediaStreamResponse = new MediaStreamResolveResponseMessage
        {
            Success = true,
            Item = new MediaStreamLocationDto
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
            $"/api/media/watch/{mediaGuid}?storageKey=storage-a");
        request.Headers.Range = new RangeHeaderValue(2, 4);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
        response.Content.Headers.ContentRange!.From.ShouldBe(2);
        response.Content.Headers.ContentRange.To.ShouldBe(4);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe([2, 3, 4]);
        factory.MessageBus.MediaStreamRequest.ShouldNotBeNull();
        factory.MessageBus.MediaStreamRequest.StorageKey.ShouldBe("storage-a");
        factory.MessageBus.MediaStreamRequest.Version.ShouldBeNull();
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
            cookieProfileKey = "member-cookie"
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
        // RequestedBy is now stamped server-side from the validated subject (single-user mode).
        published.Message.RequestedBy.ShouldBe(AuthConstants.SingleUserSubject);
        // Single-user mode resolves the subject to the synthetic owner, so the cookie profile lands
        // under that owner's user-scoped path — never a global key.
        published.Message.CookieSecretPath.ShouldBe($"cookies/users/{AuthConstants.SingleUserSubject}/member-cookie");
        published.Message.MediaKind.ShouldBe(MediaKind.Audio);
        published.Message.AudioFormat.ShouldBe(AudioConversionFormat.Mp3);
        published.Message.OccurredAt.ShouldBe(TestWebApiFactory.Now);
    }

    [Test]
    public async Task Post_Schedule_With_Invalid_Trigger_Returns_Model_Validation_400()
    {
        using var factory = new TestWebApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/global/schedules", new
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
    public async Task Put_Cookie_Writes_To_User_Scoped_Secret_Path_Through_Http_Surface()
    {
        using var factory = new TestWebApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/user/cookies/member-cookie", new
        {
            content = "# Netscape HTTP Cookie File",
            site = "example.com"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Single-user mode resolves the subject to the synthetic owner, so the body lands under the
        // user-scoped path — never the legacy global cookies/{key}.
        factory.SecretStore.Secrets[$"cookies/users/{AuthConstants.SingleUserSubject}/member-cookie"]["content"]
            .ShouldBe("# Netscape HTTP Cookie File");
        factory.MessageBus.LastCookieUpsert.ShouldNotBeNull();
        factory.MessageBus.LastCookieUpsert!.ProfileKey.ShouldBe("member-cookie");
        factory.MessageBus.LastCookieUpsert.OwnerSubject.ShouldBe(AuthConstants.SingleUserSubject);
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
    public MediaStreamResolveResponseMessage? MediaStreamResponse { get; set; }
    public MediaStreamResolveRequestMessage? MediaStreamRequest { get; private set; }
    public CookieProfileUpsertRequestMessage? LastCookieUpsert { get; private set; }

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
        if (subject == MediaAccessSubjects.Check && request is MediaAccessCheckRequestMessage)
        {
            return Task.FromResult((TResponse?)(object)new MediaAccessCheckResponseMessage
            {
                IsAllowed = true
            });
        }

        if (subject == MediaStreamSubjects.Resolve &&
            request is MediaStreamResolveRequestMessage mediaStreamRequest &&
            MediaStreamResponse is not null)
        {
            MediaStreamRequest = mediaStreamRequest;
            return Task.FromResult((TResponse?)(object)MediaStreamResponse);
        }

        if (subject == CookieProfileSubjects.Upsert && request is CookieProfileUpsertRequestMessage cookieUpsert)
        {
            LastCookieUpsert = cookieUpsert;
            return Task.FromResult((TResponse?)(object)new CookieProfileOperationResponseMessage
            {
                Success = true,
                Entity = new CookieProfileDto
                {
                    Id = Guid.NewGuid(),
                    OwnerSubject = cookieUpsert.OwnerSubject,
                    ProfileKey = cookieUpsert.ProfileKey,
                    Site = cookieUpsert.Site,
                    DisplayName = cookieUpsert.DisplayName,
                    CreatedAt = TestWebApiFactory.Now
                }
            });
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
