using System.Security.Claims;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Shared.Auth;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Imports.Controllers;
using WebAPI.Features.Imports.Models;

namespace UnitTests.WebAPI;

public sealed class ImportsControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 3, 12, 0);

    [Test]
    public async Task ImportLocalMedia_Stores_Manifest_And_Publishes_Request()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var objectStore = new InMemoryObjectStore();
        var controller = CreateController(publisher, objectStore);

        var result = await controller.ImportLocalMedia(new LocalMediaImportRequest
        {
            Manifest = FormFile("""{"items":[{"file":"clip.mp4"}]}"""),
            SourceRoot = "/mnt/imports/batch-a",
            StorageKey = "storage-a",
            RequestedBy = "operator-note"
        }, CancellationToken.None);

        var payload = result.Result.ShouldBeOfType<AcceptedResult>().Value
            .ShouldBeOfType<LocalMediaImportRequestResponse>();
        payload.BatchId.ShouldNotBe(Guid.Empty);
        payload.CorrelationId.ShouldNotBe(Guid.Empty);

        var expectedObjectKey = $"local-media/{payload.BatchId:N}/manifest.json";
        objectStore.Objects.Keys.ShouldContain(expectedObjectKey);

        await publisher.Received(1).PublishAsync(
            LocalImportSubjects.LocalMediaImportRequested,
            Arg.Is<LocalMediaImportRequested>(x =>
                x.JobId == payload.BatchId &&
                x.BatchId == payload.BatchId &&
                x.CorrelationId == payload.CorrelationId &&
                x.OperationKey == $"local-import/{payload.BatchId:N}/requested" &&
                x.OccurredAt == Now &&
                x.ManifestObjectBucket == LocalImportTopology.ManifestObjectStoreBucket &&
                x.ManifestObjectKey == expectedObjectKey &&
                x.SourceRoot == "/mnt/imports/batch-a" &&
                x.StorageKey == "storage-a" &&
                x.RequestedBy == "unit_test_user" &&
                x.RequestedByContext == "operator-note"),
            Arg.Is<string>(x => x.Length == 32),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImportLocalMedia_Rejects_Missing_Manifest()
    {
        var publisher = Substitute.For<IJetStreamPublisher>();
        var objectStore = new InMemoryObjectStore();
        var controller = CreateController(publisher, objectStore);

        var result = await controller.ImportLocalMedia(new LocalMediaImportRequest
        {
            SourceRoot = "/mnt/imports",
            StorageKey = "storage-a"
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(LocalMediaImportRequested)!, default, default, default);
        objectStore.Objects.ShouldBeEmpty();
    }

    private static ImportsController CreateController(IJetStreamPublisher publisher, IObjectStore objectStore)
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        var controller = new ImportsController(
            publisher,
            _ => objectStore,
            clock,
            Substitute.For<ILogger<ImportsController>>());

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthConstants.SubjectClaim, "unit_test_user")], "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }

    private static IFormFile FormFile(string body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "manifest", "manifest.json");
    }

    private sealed class InMemoryObjectStore : IObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<string> PutAsync(string key, Stream data, CancellationToken cancellationToken = default)
        {
            await using var copy = new MemoryStream();
            await data.CopyToAsync(copy, cancellationToken);
            Objects[key] = copy.ToArray();
            return key;
        }

        public Task GetAsync(string key, Stream target, CancellationToken cancellationToken = default)
        {
            var bytes = Objects[key];
            return target.WriteAsync(bytes, cancellationToken).AsTask();
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<ObjectInfo?> GetInfoAsync(string key, bool showDeleted = false, CancellationToken cancellationToken = default)
            => Task.FromResult<ObjectInfo?>(null);

        public Task UpdateMetaAsync(string key, ObjectMetaInfo meta, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IEnumerable<ObjectInfo>> ListAsync(bool showDeleted = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<ObjectInfo>>([]);

        public Task WatchAsync(Func<ObjectInfo, Task> handler, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
