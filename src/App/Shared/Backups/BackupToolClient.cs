using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Shared.Backups;

/// <summary>
/// Shared launcher for the BackupTool CLI. Resolves effective connection settings (merging the
/// <c>Backup</c> section with the <c>froststreamdb</c> connection string), builds the standard
/// PostgreSQL/OpenBao argument set, and runs the tool — replacing the logic that was previously
/// duplicated in WebAPI's BackupJobService and DataBridge's BackupRunner.
/// </summary>
public sealed class BackupToolClient(IBackupToolSettings settings, IConfiguration configuration)
{
    /// <summary>Runs the tool with the given command arguments and returns its stdout.</summary>
    public async Task<string> RunAsync(IReadOnlyList<string> commandArguments, CancellationToken cancellationToken = default)
    {
        var effective = Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = effective.ToolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildArguments(effective, commandArguments))
            startInfo.ArgumentList.Add(argument);

        AddEnvironment(startInfo, effective);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Backup tool failed to start.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);

        return stdout;
    }

    /// <summary>Builds the exact command line an operator would run, for display (never executed here).</summary>
    public string BuildCommandString(IReadOnlyList<string> commandArguments)
    {
        var effective = Resolve();
        return string.Join(' ', new[] { effective.ToolPath }.Concat(BuildArguments(effective, commandArguments)).Select(Quote));
    }

    private IReadOnlyList<string> BuildArguments(EffectiveOptions effective, IReadOnlyList<string> commandArguments)
    {
        var args = SplitArguments(effective.ToolBaseArguments);
        args.AddRange(commandArguments);
        args.AddRange(PostgresArguments(effective));
        args.AddRange(OpenBaoArguments(effective));
        return args;
    }

    private static IEnumerable<string> PostgresArguments(EffectiveOptions effective)
    {
        yield return "--postgres-host";
        yield return effective.PostgresHost;
        yield return "--postgres-port";
        yield return effective.PostgresPort.ToString(CultureInfo.InvariantCulture);
        yield return "--postgres-user";
        yield return effective.PostgresUser;

        if (!string.IsNullOrWhiteSpace(effective.PostgresBinDir))
        {
            yield return "--postgres-bin-dir";
            yield return effective.PostgresBinDir;
        }

        if (!string.IsNullOrWhiteSpace(effective.ArchiveDir))
        {
            yield return "--archive-dir";
            yield return effective.ArchiveDir;
        }
    }

    private static IEnumerable<string> OpenBaoArguments(EffectiveOptions effective)
    {
        yield return "--openbao-address";
        yield return effective.OpenBaoAddress;
        yield return "--openbao-kv-mount";
        yield return effective.OpenBaoKvMount;
    }

    private static void AddEnvironment(ProcessStartInfo startInfo, EffectiveOptions effective)
    {
        if (!string.IsNullOrWhiteSpace(effective.PostgresPassword))
            startInfo.Environment["POSTGRES_PASSWORD"] = effective.PostgresPassword;
        if (!string.IsNullOrWhiteSpace(effective.OpenBaoToken))
            startInfo.Environment["OPENBAO_TOKEN"] = effective.OpenBaoToken;
    }

    private EffectiveOptions Resolve()
    {
        var connectionParts = ParseConnectionString(configuration.GetConnectionString("froststreamdb"));
        var backupSection = configuration.GetSection("Backup");

        return new EffectiveOptions(
            settings.ToolPath,
            settings.ToolBaseArguments,
            backupSection["PostgresHost"] ?? GetPart(connectionParts, "Host", "Server") ?? settings.PostgresHost,
            int.TryParse(backupSection["PostgresPort"] ?? GetPart(connectionParts, "Port"), out var port) ? port : settings.PostgresPort,
            backupSection["PostgresUser"] ?? GetPart(connectionParts, "Username", "User ID", "User") ?? settings.PostgresUser,
            backupSection["PostgresPassword"] ?? GetPart(connectionParts, "Password") ?? settings.PostgresPassword,
            settings.PostgresBinDir,
            settings.ArchiveDir,
            settings.OpenBaoAddress,
            settings.OpenBaoToken,
            settings.OpenBaoKvMount);
    }

    private static Dictionary<string, string> ParseConnectionString(string? connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connectionString))
            return result;

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2)
                result[parts[0].Trim()] = parts[1].Trim();
        }

        return result;
    }

    private static string? GetPart(IReadOnlyDictionary<string, string> parts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static List<string> SplitArguments(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Quote(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    private sealed record EffectiveOptions(
        string ToolPath,
        string ToolBaseArguments,
        string PostgresHost,
        int PostgresPort,
        string PostgresUser,
        string? PostgresPassword,
        string? PostgresBinDir,
        string? ArchiveDir,
        string OpenBaoAddress,
        string? OpenBaoToken,
        string OpenBaoKvMount);
}
