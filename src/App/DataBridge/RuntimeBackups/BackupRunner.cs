using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataBridge.Backup;

public sealed class BackupRunner(
    IOptions<BackupRunnerOptions> options,
    IConfiguration configuration,
    ILogger<BackupRunner> logger)
{
    public async Task<(bool Success, string? ArchivePath, string? ErrorMessage)> RunAsync(
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(options.Value.Directory);

            var output = await RunToolAsync(
                ["create", "--output", options.Value.Directory, "--name", name],
                cancellationToken);
            var archivePath = output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();

            return (true, archivePath, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup runner failed for backup {BackupName}.", name);
            return (false, null, ex.Message);
        }
    }

    private async Task<string> RunToolAsync(IReadOnlyList<string> commandArguments, CancellationToken cancellationToken)
    {
        var effective = BuildEffectiveOptions();
        var startInfo = new ProcessStartInfo
        {
            FileName = effective.ToolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in BuildArguments(effective, commandArguments))
            startInfo.ArgumentList.Add(argument);

        AddToolEnvironment(startInfo, effective);

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

    private static IReadOnlyList<string> BuildArguments(
        EffectiveBackupOptions effective,
        IReadOnlyList<string> commandArguments)
    {
        var args = SplitArguments(effective.ToolBaseArguments);
        args.AddRange(commandArguments);
        args.AddRange(PostgresArguments(effective));
        args.AddRange(OpenBaoArguments(effective));
        return args;
    }

    private static IEnumerable<string> PostgresArguments(EffectiveBackupOptions effective)
    {
        yield return "--postgres-host";
        yield return effective.PostgresHost;
        yield return "--postgres-port";
        yield return effective.PostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--postgres-user";
        yield return effective.PostgresUser;

        if (!string.IsNullOrWhiteSpace(effective.PostgresBinDir))
        {
            yield return "--postgres-bin-dir";
            yield return effective.PostgresBinDir;
        }
    }

    private static IEnumerable<string> OpenBaoArguments(EffectiveBackupOptions effective)
    {
        yield return "--openbao-address";
        yield return effective.OpenBaoAddress;
        yield return "--openbao-kv-mount";
        yield return effective.OpenBaoKvMount;
    }

    private static void AddToolEnvironment(ProcessStartInfo startInfo, EffectiveBackupOptions effective)
    {
        if (!string.IsNullOrWhiteSpace(effective.PostgresPassword))
            startInfo.Environment["POSTGRES_PASSWORD"] = effective.PostgresPassword;

        if (!string.IsNullOrWhiteSpace(effective.OpenBaoToken))
            startInfo.Environment["OPENBAO_TOKEN"] = effective.OpenBaoToken;
    }

    private EffectiveBackupOptions BuildEffectiveOptions()
    {
        var connectionParts = ParseConnectionString(configuration.GetConnectionString("froststreamdb"));
        var backupSection = configuration.GetSection(BackupRunnerOptions.SectionName);
        var configured = options.Value;

        return new EffectiveBackupOptions(
            configured.ToolPath,
            configured.ToolBaseArguments,
            backupSection["PostgresHost"] ?? GetPart(connectionParts, "Host", "Server") ?? configured.PostgresHost,
            int.TryParse(backupSection["PostgresPort"] ?? GetPart(connectionParts, "Port"), out var port)
                ? port
                : configured.PostgresPort,
            backupSection["PostgresUser"] ?? GetPart(connectionParts, "Username", "User ID", "User") ?? configured.PostgresUser,
            backupSection["PostgresPassword"] ?? GetPart(connectionParts, "Password") ?? configured.PostgresPassword,
            configured.PostgresBinDir,
            configured.OpenBaoAddress,
            configured.OpenBaoToken,
            configured.OpenBaoKvMount);
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

    private sealed record EffectiveBackupOptions(
        string ToolPath,
        string ToolBaseArguments,
        string PostgresHost,
        int PostgresPort,
        string PostgresUser,
        string? PostgresPassword,
        string? PostgresBinDir,
        string OpenBaoAddress,
        string? OpenBaoToken,
        string OpenBaoKvMount);
}
