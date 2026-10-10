namespace YtDlpSharpLib.Process;

/// <summary>
/// Coordinates spacing between yt-dlp child process starts.
/// </summary>
public interface IYtDlpProcessStartGate
{
    /// <summary>Waits until another yt-dlp process may be started.</summary>
    ValueTask WaitForTurnAsync(CancellationToken ct);
}
