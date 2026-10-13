using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
using YtDlpSharpLib.Exceptions;
using SystemProcess = System.Diagnostics.Process;

namespace YtDlpSharpLib.Process;

/// <summary>
/// Default <see cref="IYtDlpProcess"/> implementation that wraps
/// <see cref="System.Diagnostics.Process"/> and exposes its output through bounded channels.
/// </summary>
public sealed class YtDlpProcess : IYtDlpProcess
{
    private const int BoundedCapacity = 256;
    private const int StateNotStarted = 0;
    private const int StateStarted = 1;
    private const int StateDisposed = 2;

    private readonly YtDlpProcessStartInfo _startInfo;
    private readonly SystemProcess _process;
    private readonly Channel<string> _stdoutChannel;
    private readonly Channel<string> _stderrChannel;
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _stdoutReaderTask;
    private Task? _stderrReaderTask;
    private int _state = StateNotStarted;

    /// <summary>Creates a new wrapper around the given start info.</summary>
    public YtDlpProcess(YtDlpProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _startInfo = startInfo;
        _process = BuildProcess(startInfo);
        _stdoutChannel = CreateChannel();
        _stderrChannel = CreateChannel();
    }

    /// <inheritdoc />
    public ChannelReader<string> StdoutLines => _stdoutChannel.Reader;

    /// <inheritdoc />
    public ChannelReader<string> StderrLines => _stderrChannel.Reader;

    /// <inheritdoc />
    public int? ExitCode => HasExited ? _process.ExitCode : null;

    /// <inheritdoc />
    public bool HasExited => _state != StateNotStarted && SafeHasExited(_process);

    /// <inheritdoc />
    public int? ProcessId
    {
        get
        {
            if (_state == StateNotStarted)
            {
                return null;
            }

            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_state == StateDisposed, this);

        if (_state != StateNotStarted)
        {
            throw new InvalidOperationException("Process has already been started.");
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            _process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new YtDlpNotFoundException(_startInfo.ExecutablePath, ex);
        }

        _state = StateStarted;

        _stdoutReaderTask = Task.Run(
            () => ReadOutputAsync(_process.StandardOutput, _stdoutChannel.Writer, _startInfo.RawStdoutWriter, _readerCts.Token),
            CancellationToken.None);

        _stderrReaderTask = Task.Run(
            () => ReadOutputAsync(_process.StandardError, _stderrChannel.Writer, _startInfo.RawStderrWriter, _readerCts.Token),
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_state == StateDisposed, this);

        if (_state == StateNotStarted)
        {
            throw new InvalidOperationException("Process has not been started.");
        }

        await _process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (_stdoutReaderTask is { } stdoutTask)
        {
            await SafeAwaitAsync(stdoutTask).ConfigureAwait(false);
        }

        if (_stderrReaderTask is { } stderrTask)
        {
            await SafeAwaitAsync(stderrTask).ConfigureAwait(false);
        }

        _stdoutChannel.Writer.TryComplete();
        _stderrChannel.Writer.TryComplete();
    }

    /// <inheritdoc />
    public void Kill(bool entireProcessTree)
    {
        if (_state != StateStarted)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill; nothing to do.
        }
        catch (Win32Exception)
        {
            // The OS refused the kill (permissions, races); best effort only.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, StateDisposed) == StateDisposed)
        {
            return;
        }

        try
        {
            if (!SafeHasExited(_process))
            {
                Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        await _readerCts.CancelAsync().ConfigureAwait(false);

        if (_stdoutReaderTask is { } stdoutTask)
        {
            await SafeAwaitAsync(stdoutTask).ConfigureAwait(false);
        }

        if (_stderrReaderTask is { } stderrTask)
        {
            await SafeAwaitAsync(stderrTask).ConfigureAwait(false);
        }

        _stdoutChannel.Writer.TryComplete();
        _stderrChannel.Writer.TryComplete();
        _readerCts.Dispose();
        _process.Dispose();
    }

    private static Channel<string> CreateChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(BoundedCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static SystemProcess BuildProcess(YtDlpProcessStartInfo startInfo)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = startInfo.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(startInfo.WorkingDirectory))
        {
            psi.WorkingDirectory = startInfo.WorkingDirectory;
        }

        foreach (var arg in startInfo.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in startInfo.EnvironmentVariables)
        {
            psi.Environment[key] = value;
        }

        return new SystemProcess
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        ChannelWriter<string> writer,
        TextWriter? sink,
        CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (sink is not null)
                {
                    await sink.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                }

                await writer.WriteAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static bool SafeHasExited(SystemProcess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Reader tasks are awaited only to drain; downstream callers receive any error via the channel reader.")]
    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
