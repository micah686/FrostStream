using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BackupServiceNew.PgBackRestWrapper;

/// <summary>Runs without a shell, drains both pipes, bounds captured tails, and kills the process tree on cancellation.</summary>
public sealed class ProcessRunner : ICommandRunner
{
    private const int CaptureLimit = 16 * 1024 * 1024;
    /// <summary>Runs a process without a shell, captures output, and terminates its process tree on cancellation or timeout.</summary>
    /// <param name="command">Executable, arguments, and timeout to run.</param><param name="progress">Optional receiver for output chunks.</param>
    /// <param name="cancellationToken">Token used to cancel the process.</param>
    public async Task<CommandResult> RunAsync(CommandSpec command, IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in command.Arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var started = DateTimeOffset.UtcNow;
        process.Start();
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        // A callback must never prevent draining a child's output.
        async Task Drain(StreamReader reader, StringBuilder capture, bool error)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
            {
                var chunk = new string(buffer, 0, read);
                capture.Append(chunk);
                if (capture.Length > CaptureLimit) capture.Remove(0, capture.Length - CaptureLimit);
                try { progress?.Report(new OutputLine(DateTimeOffset.UtcNow, error, chunk)); } catch { }
            }
        }
        var outputTask = Drain(process.StandardOutput, stdout, false);
        var errorTask = Drain(process.StandardError, stderr, true);
        var cancelled = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(outputTask, errorTask);
        return new CommandResult(Guid.NewGuid(), command, started, DateTimeOffset.UtcNow, process.ExitCode,
            stdout.ToString(), stderr.ToString(), cancelled && cancellationToken.IsCancellationRequested,
            cancelled && timeout.IsCancellationRequested);
    }
}

/// <summary>Stores JSON operation reports in the configured state directory.</summary>
/// <param name="path">Directory used to read and write report files.</param>
internal sealed class ReportStore(string path)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    /// <summary>Atomically serializes a report into the state directory.</summary>
    /// <typeparam name="T">Report value type.</typeparam><param name="name">Report filename.</param><param name="value">Report value to serialize.</param>
    internal async Task WriteAsync<T>(string name, T value)
    {
        Paths.Writable(path);
        var temporary = Path.Combine(path, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
                await stream.FlushAsync(); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(path, name), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Reads and deserializes a report from the state directory.</summary>
    /// <typeparam name="T">Expected report type.</typeparam><param name="name">Report filename.</param>
    internal async Task<T> ReadAsync<T>(string name)
    {
        await using var stream = File.OpenRead(Path.Combine(path, name));
        return (await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions))!;
    }
}
