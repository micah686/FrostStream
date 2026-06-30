using FluentStorage.Blobs;
using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Media.Controllers;

namespace UnitTests.WebAPI;

public sealed class MediaStreamControllerTests
{
    [Test]
    public async Task GetStream_Resolves_Filters_And_Returns_Range_Enabled_Stream()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IBlobStorage>();
        var stream = new MemoryStream([1, 2, 3]);

        bus.RequestAsync<MediaStreamResolveRequestMessage, MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                Arg.Is<MediaStreamResolveRequestMessage>(request =>
                    request.MediaGuid == mediaGuid &&
                    request.StorageKey == "storage-a" &&
                    request.Version == 2),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaStreamResolveResponseMessage
            {
                Success = true,
                Item = Location(mediaGuid, "storage-a", "media/video.mp4", 2)
            });
        provider.GetAsync("storage-a", Arg.Any<CancellationToken>()).Returns(storage);
        storage.OpenReadAsync("media/video.mp4", Arg.Any<CancellationToken>()).Returns(stream);

        var result = await CreateController(bus, provider).GetStream(
            mediaGuid,
            " storage-a ",
            2,
            CancellationToken.None);

        var file = result.ShouldBeOfType<FileStreamResult>();
        file.FileStream.ShouldBeSameAs(stream);
        file.ContentType.ShouldBe("video/mp4");
        file.EnableRangeProcessing.ShouldBeTrue();
    }

    [Test]
    public async Task GetStream_Disables_Ranges_For_NonSeekable_Stream_And_Uses_Binary_Mime()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IBlobStorage>();
        var stream = new NonSeekableReadStream([1, 2, 3]);

        ArrangeResolved(bus, mediaGuid, Location(mediaGuid, "storage-a", "media/content.unknown", 1));
        provider.GetAsync("storage-a", Arg.Any<CancellationToken>()).Returns(storage);
        storage.OpenReadAsync("media/content.unknown", Arg.Any<CancellationToken>()).Returns(stream);

        var result = await CreateController(bus, provider).GetStream(mediaGuid);

        var file = result.ShouldBeOfType<FileStreamResult>();
        file.ContentType.ShouldBe("application/octet-stream");
        file.EnableRangeProcessing.ShouldBeFalse();
    }

    [Test]
    public async Task GetStream_Validates_Version_And_Maps_Lookup_Failures()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var controller = CreateController(bus, provider);

        var invalid = await controller.GetStream(mediaGuid, version: 0);
        invalid.ShouldBeOfType<BadRequestObjectResult>();

        bus.RequestAsync<MediaStreamResolveRequestMessage, MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                Arg.Any<MediaStreamResolveRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaStreamResolveResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var missing = await controller.GetStream(mediaGuid);
        missing.ShouldBeOfType<NotFoundObjectResult>().Value.ShouldBe("missing");
    }

    [Test]
    public async Task GetStream_Maps_Missing_Object_And_Provider_Failure()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IBlobStorage>();
        var location = Location(mediaGuid, "storage-a", "media/video.mp4", 1);
        ArrangeResolved(bus, mediaGuid, location);
        provider.GetAsync("storage-a", Arg.Any<CancellationToken>()).Returns(storage);
        storage.OpenReadAsync(location.StoragePath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(null!));

        var controller = CreateController(bus, provider);
        var missing = await controller.GetStream(mediaGuid);
        missing.ShouldBeOfType<NotFoundObjectResult>();

        provider.GetAsync("storage-a", Arg.Any<CancellationToken>())
            .Returns<Task<IBlobStorage>>(_ => throw new InvalidOperationException("storage failed"));

        var failed = await controller.GetStream(mediaGuid);
        failed.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task GetStream_Returns_403_When_Watch_Access_Is_Denied()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        ArrangeResolved(bus, mediaGuid, Location(mediaGuid, "storage-a", "media/video.mp4", 1));

        // Override the default-allowed access check with a denial.
        var controller = CreateController(bus, provider);
        bus.RequestAsync<MediaAccessCheckRequestMessage, MediaAccessCheckResponseMessage>(
                MediaAccessSubjects.Check,
                Arg.Any<MediaAccessCheckRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaAccessCheckResponseMessage { IsAllowed = false, FailureReason = "media-restricted" });

        var result = await controller.GetStream(mediaGuid);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await provider.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static MediaStreamController CreateController(
        IMessageBus bus,
        IBlobStorageProvider provider)
    {
        // The stream endpoints gate on a watch-time access check; default it to allowed so these tests
        // exercise the streaming path. See MediaAccessControllerTests for the restriction behaviour.
        bus.RequestAsync<MediaAccessCheckRequestMessage, MediaAccessCheckResponseMessage>(
                MediaAccessSubjects.Check,
                Arg.Any<MediaAccessCheckRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaAccessCheckResponseMessage { IsAllowed = true });

        return new MediaStreamController(bus, provider, Substitute.For<ILogger<MediaStreamController>>());
    }

    private static void ArrangeResolved(
        IMessageBus bus,
        Guid mediaGuid,
        MediaStreamLocationDto location)
    {
        bus.RequestAsync<MediaStreamResolveRequestMessage, MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                Arg.Is<MediaStreamResolveRequestMessage>(request => request.MediaGuid == mediaGuid),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaStreamResolveResponseMessage
            {
                Success = true,
                Item = location
            });
    }

    private static MediaStreamLocationDto Location(
        Guid mediaGuid,
        string storageKey,
        string storagePath,
        int version)
        => new()
        {
            MediaGuid = mediaGuid,
            StorageKey = storageKey,
            StoragePath = storagePath,
            Version = version
        };

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
