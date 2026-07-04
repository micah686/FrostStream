using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;
using CastMedia = Sharpcaster.Models.Media.Media;

namespace WebAPI.Features.Media.Casting;

/// <summary>
/// Seam over Sharpcaster's <see cref="ChromecastClient"/> so <see cref="CastSessionManager"/> can be
/// unit-tested without a device. One instance corresponds to one TCP connection to one receiver.
/// </summary>
public interface ICastSessionClient : IAsyncDisposable
{
    event EventHandler<MediaStatus?>? MediaStatusChanged;
    event EventHandler<ChromecastStatus?>? ReceiverStatusChanged;
    event EventHandler? Disconnected;

    bool HasMediaSession { get; }

    Task ConnectAsync(ChromecastReceiver receiver);
    Task LaunchDefaultReceiverAsync();
    Task<MediaStatus?> GetMediaStatusAsync();
    Task<MediaStatus?> LoadAsync(CastMedia media, int[]? activeTrackIds);
    Task<MediaStatus?> PlayAsync();
    Task<MediaStatus?> PauseAsync();
    Task<MediaStatus?> StopAsync();
    Task<MediaStatus?> SeekAsync(double seconds);
    Task SetVolumeAsync(double level);
    Task SetMutedAsync(bool muted);
    Task DisconnectAsync();
}

public interface ICastSessionClientFactory
{
    ICastSessionClient Create();
}

public sealed class SharpcasterSessionClientFactory(ILoggerFactory loggerFactory) : ICastSessionClientFactory
{
    public ICastSessionClient Create()
        => new SharpcasterSessionClient(
            loggerFactory.CreateLogger<ChromecastClient>(),
            loggerFactory.CreateLogger<SharpcasterSessionClient>());
}

public sealed class SharpcasterSessionClient : ICastSessionClient
{
    /// <summary>Google's Default Media Receiver application id.</summary>
    public const string DefaultMediaReceiverAppId = "CC1AD845";

    private readonly ChromecastClient _client;
    private readonly ILogger<SharpcasterSessionClient> _logger;

    public SharpcasterSessionClient(
        ILogger<ChromecastClient> chromecastLogger,
        ILogger<SharpcasterSessionClient> logger)
    {
        _logger = logger;
        _client = new ChromecastClient(chromecastLogger);
        _client.MediaChannel.StatusChanged += (sender, status) => MediaStatusChanged?.Invoke(this, status);
        _client.MediaChannel.LoadFailed += (_, message) =>
            _logger.LogWarning("Chromecast reported LOAD_FAILED for media request {RequestId} item {ItemId}.", message.RequestId, message.ItemId);
        _client.MediaChannel.LoadCancelled += (_, message) =>
            _logger.LogWarning("Chromecast reported LOAD_CANCELLED for media request {RequestId}.", message.RequestId);
        _client.MediaChannel.InvalidRequest += (_, message) =>
            _logger.LogWarning("Chromecast reported INVALID_REQUEST for media request {RequestId}: {Reason}.", message.RequestId, message.Reason);
        _client.MediaChannel.ErrorHappened += (_, message) =>
            _logger.LogWarning("Chromecast reported media ERROR code {DetailedErrorCode} for item {ItemId}.", message.DetailedErrorCode, message.ItemId);
        _client.ReceiverChannel.ReceiverStatusChanged += (sender, status) => ReceiverStatusChanged?.Invoke(this, status);
        _client.Disconnected += (sender, args) => Disconnected?.Invoke(this, args);
    }

    public event EventHandler<MediaStatus?>? MediaStatusChanged;
    public event EventHandler<ChromecastStatus?>? ReceiverStatusChanged;
    public event EventHandler? Disconnected;

    public bool HasMediaSession => _client.MediaStatus is not null;

    public Task ConnectAsync(ChromecastReceiver receiver) => _client.ConnectChromecast(receiver);

    public Task LaunchDefaultReceiverAsync() => _client.LaunchApplicationAsync(DefaultMediaReceiverAppId);

    public async Task<MediaStatus?> GetMediaStatusAsync()
        => await _client.MediaChannel.GetMediaStatusAsync();

    public async Task<MediaStatus?> LoadAsync(CastMedia media, int[]? activeTrackIds)
        => await _client.MediaChannel.LoadAsync(media, autoPlay: true, activeTrackIds);

    public async Task<MediaStatus?> PlayAsync() => await _client.MediaChannel.PlayAsync();

    public async Task<MediaStatus?> PauseAsync() => await _client.MediaChannel.PauseAsync();

    public async Task<MediaStatus?> StopAsync() => await _client.MediaChannel.StopAsync();

    public async Task<MediaStatus?> SeekAsync(double seconds) => await _client.MediaChannel.SeekAsync(seconds);

    public Task SetVolumeAsync(double level) => _client.ReceiverChannel.SetVolume(level);

    public Task SetMutedAsync(bool muted) => _client.ReceiverChannel.SetMute(muted);

    public Task DisconnectAsync() => _client.DisconnectAsync();

    public async ValueTask DisposeAsync() => await _client.Dispose();
}
