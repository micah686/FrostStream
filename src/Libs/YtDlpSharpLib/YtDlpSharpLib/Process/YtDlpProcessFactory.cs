namespace YtDlpSharpLib.Process;

/// <summary>
/// Default <see cref="IYtDlpProcessFactory"/> that returns a fresh <see cref="YtDlpProcess"/>
/// for every call.
/// </summary>
public sealed class YtDlpProcessFactory : IYtDlpProcessFactory
{
    /// <inheritdoc />
    public IYtDlpProcess Create(YtDlpProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return new YtDlpProcess(startInfo);
    }
}
