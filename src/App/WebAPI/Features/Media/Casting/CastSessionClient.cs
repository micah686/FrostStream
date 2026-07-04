using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;
using CastMedia = Sharpcaster.Models.Media.Media;

namespace WebAPI.Features.Media.Casting;

/// <summary>
/// Common transport client used by the session manager. One instance corresponds to one live
/// connection to one receiver, regardless of the protocol used to reach it.
/// </summary>
public interface ICastSessionClient : IAsyncDisposable
{
    event EventHandler<CastSessionSnapshot?>? StatusChanged;
    event EventHandler? Disconnected;

    bool HasMediaSession { get; }

    Task ConnectAsync();
    Task<CastSessionSnapshot?> GetStatusAsync();
    Task<CastSessionSnapshot?> LoadAsync(CastLoadSpec spec);
    Task<CastSessionSnapshot?> PlayAsync();
    Task<CastSessionSnapshot?> PauseAsync();
    Task<CastSessionSnapshot?> StopAsync();
    Task<CastSessionSnapshot?> SeekAsync(double seconds);
    Task SetVolumeAsync(double level);
    Task SetMutedAsync(bool muted);
    Task DisconnectAsync();
}

public sealed class SharpcasterSessionClient : ICastSessionClient
{
    /// <summary>Google's Default Media Receiver application id.</summary>
    public const string DefaultMediaReceiverAppId = "CC1AD845";

    private readonly ChromecastReceiver _receiver;
    private readonly ChromecastClient _client;
    private readonly ILogger<SharpcasterSessionClient> _logger;

    public SharpcasterSessionClient(
        ChromecastReceiver receiver,
        ILogger<ChromecastClient> chromecastLogger,
        ILogger<SharpcasterSessionClient> logger)
    {
        _receiver = receiver;
        _logger = logger;
        _client = new ChromecastClient(chromecastLogger);
        _client.MediaChannel.StatusChanged += (sender, status) => StatusChanged?.Invoke(this, ToSnapshot(status));
        _client.MediaChannel.LoadFailed += (_, message) =>
            _logger.LogWarning("Chromecast reported LOAD_FAILED for media request {RequestId} item {ItemId}.", message.RequestId, message.ItemId);
        _client.MediaChannel.LoadCancelled += (_, message) =>
            _logger.LogWarning("Chromecast reported LOAD_CANCELLED for media request {RequestId}.", message.RequestId);
        _client.MediaChannel.InvalidRequest += (_, message) =>
            _logger.LogWarning("Chromecast reported INVALID_REQUEST for media request {RequestId}: {Reason}.", message.RequestId, message.Reason);
        _client.MediaChannel.ErrorHappened += (_, message) =>
            _logger.LogWarning("Chromecast reported media ERROR code {DetailedErrorCode} for item {ItemId}.", message.DetailedErrorCode, message.ItemId);
        _client.ReceiverChannel.ReceiverStatusChanged += (sender, status) => StatusChanged?.Invoke(this, ToSnapshot(status));
        _client.Disconnected += (sender, args) => Disconnected?.Invoke(this, args);
    }

    public event EventHandler<CastSessionSnapshot?>? StatusChanged;
    public event EventHandler? Disconnected;

    public bool HasMediaSession => _client.MediaStatus is not null;

    public async Task ConnectAsync()
    {
        await _client.ConnectChromecast(_receiver);
        await _client.LaunchApplicationAsync(DefaultMediaReceiverAppId);
    }

    public async Task<CastSessionSnapshot?> GetStatusAsync()
        => ToSnapshot(await _client.MediaChannel.GetMediaStatusAsync());

    public async Task<CastSessionSnapshot?> LoadAsync(CastLoadSpec spec)
    {
        var media = ToMedia(spec);
        var status = await _client.MediaChannel.LoadAsync(media, autoPlay: true, ActiveTrackIds(spec));

        // Sharpcaster's load request has no start position, so seek right after media is loaded.
        if (spec.StartPositionSeconds is { } position && position > 0)
        {
            status = await _client.MediaChannel.SeekAsync(position);
        }

        return ToSnapshot(status);
    }

    public async Task<CastSessionSnapshot?> PlayAsync() => ToSnapshot(await _client.MediaChannel.PlayAsync());

    public async Task<CastSessionSnapshot?> PauseAsync() => ToSnapshot(await _client.MediaChannel.PauseAsync());

    public async Task<CastSessionSnapshot?> StopAsync() => ToSnapshot(await _client.MediaChannel.StopAsync());

    public async Task<CastSessionSnapshot?> SeekAsync(double seconds) => ToSnapshot(await _client.MediaChannel.SeekAsync(seconds));

    public Task SetVolumeAsync(double level) => _client.ReceiverChannel.SetVolume(level);

    public Task SetMutedAsync(bool muted) => _client.ReceiverChannel.SetMute(muted);

    public Task DisconnectAsync() => _client.DisconnectAsync();

    public async ValueTask DisposeAsync() => await _client.Dispose();

    private static CastMedia ToMedia(CastLoadSpec spec)
    {
        var media = new CastMedia
        {
            ContentUrl = spec.ContentUrl,
            ContentType = spec.ContentType,
            StreamType = StreamType.Buffered,
            Duration = spec.DurationSeconds,
            Metadata = new MediaMetadata
            {
                MetadataType = MetadataType.Default,
                Title = spec.Title,
                Images = spec.ThumbnailUrl is null ? null : [new Image { Url = spec.ThumbnailUrl }]
            }
        };

        if (spec.Subtitle is { } subtitle)
        {
            media.Tracks =
            [
                new Track
                {
                    TrackId = 1,
                    Type = TrackType.TEXT,
                    Subtype = TextTrackType.SUBTITLES,
                    Language = subtitle.Language,
                    Name = subtitle.Language,
                    TrackContentId = subtitle.Url,
                    TrackContentType = subtitle.ContentType
                }
            ];
        }

        return media;
    }

    private static int[]? ActiveTrackIds(CastLoadSpec spec) => spec.Subtitle is null ? null : [1];

    private static CastSessionSnapshot? ToSnapshot(MediaStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        return new CastSessionSnapshot
        {
            PlayerState = status.PlayerState.ToString(),
            CurrentTime = status.CurrentTime,
            DurationSeconds = status.Media?.Duration,
            VolumeLevel = status.Volume?.Level,
            Muted = status.Volume?.Muted,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static CastSessionSnapshot? ToSnapshot(ChromecastStatus? status)
    {
        if (status?.Volume is null)
        {
            return null;
        }

        return new CastSessionSnapshot
        {
            PlayerState = "Unknown",
            VolumeLevel = status.Volume.Level,
            Muted = status.Volume.Muted,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
