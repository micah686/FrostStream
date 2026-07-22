using FluentStorage.Storage;
using Conduit.NATS;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Messaging;
using Shared.Storage;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Media;
using WebAPI.Features.Media.Controllers;

namespace UnitTests.WebAPI;

public sealed class MediaWatchControllerTests
{
    [Test]
    public async Task GetWatch_Resolves_Filters_And_Returns_Range_Enabled_Stream()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IStore>();
        var stream = new MemoryStream([1, 2, 3]);

        bus.RequestAsync<MediaStreamResolveRequestMessage, MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                Arg.Is<MediaStreamResolveRequestMessage>(request => request != null &&
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
        storage.OpenRead("media/video.mp4", Arg.Any<CancellationToken>()).Returns(stream);

        var result = await CreateController(bus, provider).GetWatch(
            mediaGuid,
            storageKey: " storage-a ",
            version: 2,
            cancellationToken: CancellationToken.None);

        var file = result.ShouldBeOfType<FileStreamResult>();
        file.FileStream.ShouldBeSameAs(stream);
        file.ContentType.ShouldBe("video/mp4");
        file.EnableRangeProcessing.ShouldBeTrue();
    }

    [Test]
    public async Task GetWatch_Disables_Ranges_For_NonSeekable_Stream_And_Uses_Binary_Mime()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IStore>();
        var stream = new NonSeekableReadStream([1, 2, 3]);

        ArrangeResolved(bus, mediaGuid, Location(mediaGuid, "storage-a", "media/content.unknown", 1));
        provider.GetAsync("storage-a", Arg.Any<CancellationToken>()).Returns(storage);
        storage.OpenRead("media/content.unknown", Arg.Any<CancellationToken>()).Returns(stream);

        var result = await CreateController(bus, provider).GetWatch(mediaGuid);

        var file = result.ShouldBeOfType<FileStreamResult>();
        file.ContentType.ShouldBe("application/octet-stream");
        file.EnableRangeProcessing.ShouldBeFalse();
    }

    [Test]
    public async Task GetWatch_Validates_Version_And_Maps_Lookup_Failures()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var controller = CreateController(bus, provider);

        var invalid = await controller.GetWatch(mediaGuid, version: 0);
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

        var missing = await controller.GetWatch(mediaGuid);
        missing.ShouldBeOfType<NotFoundObjectResult>().Value!.ShouldBe("missing");
    }

    [Test]
    public async Task GetWatch_Audio_Returns_202_While_Rendition_Prepares()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();

        bus.RequestAsync<AudioRenditionResolveRequest, AudioRenditionResolveResponse>(
                AudioRenditionSubjects.Resolve,
                Arg.Is<AudioRenditionResolveRequest>(request => request != null &&
                    request.MediaGuid == mediaGuid &&
                    request.CreateIfMissing),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AudioRenditionResolveResponse
            {
                Success = true,
                Item = new AudioRenditionDto
                {
                    RenditionId = Guid.NewGuid(),
                    MediaGuid = mediaGuid,
                    SourceVersion = 1,
                    Status = AudioRenditionStatus.Pending,
                    StorageKey = "storage-a"
                }
            });

        var result = await CreateController(bus, provider).GetWatch(mediaGuid, audio: true);

        result.ShouldBeOfType<AcceptedResult>();
    }

    [Test]
    public async Task GetWatch_Maps_Missing_Object_And_Provider_Failure()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IStore>();
        var location = Location(mediaGuid, "storage-a", "media/video.mp4", 1);
        ArrangeResolved(bus, mediaGuid, location);
        provider.GetAsync("storage-a", Arg.Any<CancellationToken>()).Returns(storage);
        storage.OpenRead(location.StoragePath, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream>(null!));

        var controller = CreateController(bus, provider);
        var missing = await controller.GetWatch(mediaGuid);
        missing.ShouldBeOfType<NotFoundObjectResult>();

        provider.GetAsync("storage-a", Arg.Any<CancellationToken>())
            .Returns<Task<IStore>>(_ => throw new InvalidOperationException("storage failed"));

        var failed = await controller.GetWatch(mediaGuid);
        failed.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status502BadGateway);
    }

    [Test]
    public async Task GetWatch_Returns_403_When_Watch_Access_Is_Denied()
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

        var result = await controller.GetWatch(mediaGuid);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await provider.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetThumbnail_Resolves_And_Returns_Cached_Image()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();
        var storage = Substitute.For<IStore>();
        var stream = new MemoryStream([1, 2, 3]);
        var location = ThumbnailLocation(mediaGuid, "storage-a", "thumbs/poster.jpg");

        ArrangeThumbnailResolved(bus, mediaGuid, location);
        provider.GetAsync("storage-a", Arg.Any<CancellationToken>()).Returns(storage);
        storage.OpenRead("thumbs/poster.jpg", Arg.Any<CancellationToken>()).Returns(stream);

        var controller = CreateController(bus, provider);
        var result = await controller.GetThumbnail(mediaGuid, CancellationToken.None);

        var file = result.ShouldBeOfType<FileStreamResult>();
        file.FileStream.ShouldBeSameAs(stream);
        file.ContentType.ShouldBe("image/jpeg");
        controller.Response.Headers.CacheControl.ToString().ShouldBe("private, max-age=86400");
    }

    [Test]
    public async Task GetThumbnail_Maps_Missing_Thumbnail_And_Denies_Access()
    {
        var mediaGuid = Guid.NewGuid();
        var bus = Substitute.For<IMessageBus>();
        var provider = Substitute.For<IBlobStorageProvider>();

        bus.RequestAsync<MediaThumbnailResolveRequestMessage, MediaThumbnailResolveResponseMessage>(
                MediaStreamSubjects.ResolveThumbnail,
                Arg.Any<MediaThumbnailResolveRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaThumbnailResolveResponseMessage
            {
                Success = false,
                ErrorCode = "not_found",
                ErrorMessage = "missing"
            });

        var controller = CreateController(bus, provider);
        var missing = await controller.GetThumbnail(mediaGuid, CancellationToken.None);
        missing.ShouldBeOfType<NotFoundObjectResult>().Value!.ShouldBe("missing");

        bus.RequestAsync<MediaAccessCheckRequestMessage, MediaAccessCheckResponseMessage>(
                MediaAccessSubjects.Check,
                Arg.Any<MediaAccessCheckRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaAccessCheckResponseMessage { IsAllowed = false, FailureReason = "media-restricted" });

        var denied = await controller.GetThumbnail(mediaGuid, CancellationToken.None);

        denied.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await provider.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static MediaWatchController CreateController(
        IMessageBus bus,
        IBlobStorageProvider provider)
    {
        // The watch endpoints gate on a watch-time access check; default it to allowed so these tests
        // exercise the streaming path. See MediaAccessControllerTests for the restriction behaviour.
        bus.RequestAsync<MediaAccessCheckRequestMessage, MediaAccessCheckResponseMessage>(
                MediaAccessSubjects.Check,
                Arg.Any<MediaAccessCheckRequestMessage>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaAccessCheckResponseMessage { IsAllowed = true });

        return new MediaWatchController(
            bus,
            provider,
            new MediaAccessChecker(bus, Substitute.For<ILogger<MediaAccessChecker>>()),
            new AudioRenditionResolver(bus, Substitute.For<ILogger<AudioRenditionResolver>>()),
            new CastTokenService(
                Options.Create(new CastTokenOptions()),
                Substitute.For<ILogger<CastTokenService>>()),
            Substitute.For<ILogger<MediaWatchController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static void ArrangeResolved(
        IMessageBus bus,
        Guid mediaGuid,
        MediaStreamLocationDto location)
    {
        bus.RequestAsync<MediaStreamResolveRequestMessage, MediaStreamResolveResponseMessage>(
                MediaStreamSubjects.Resolve,
                Arg.Is<MediaStreamResolveRequestMessage>(request => request != null && request.MediaGuid == mediaGuid),
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

    private static void ArrangeThumbnailResolved(
        IMessageBus bus,
        Guid mediaGuid,
        MediaThumbnailLocationDto location)
    {
        bus.RequestAsync<MediaThumbnailResolveRequestMessage, MediaThumbnailResolveResponseMessage>(
                MediaStreamSubjects.ResolveThumbnail,
                Arg.Is<MediaThumbnailResolveRequestMessage>(request => request != null && request.MediaGuid == mediaGuid),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new MediaThumbnailResolveResponseMessage
            {
                Success = true,
                Item = location
            });
    }

    private static MediaThumbnailLocationDto ThumbnailLocation(
        Guid mediaGuid,
        string storageKey,
        string storagePath)
        => new()
        {
            MediaGuid = mediaGuid,
            StorageKey = storageKey,
            StoragePath = storagePath
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
