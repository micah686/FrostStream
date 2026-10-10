using System.Diagnostics;
using System.Globalization;

namespace BackupService.PgBackRest;

/// <summary>
/// Thin wrapper around the pgbackrest CLI. Configuration (stanza paths, repo, retention,
/// compression) lives in /etc/pgbackrest/pgbackrest.conf, shared with the postgres container;
/// only per-invocation options are passed on the command line. The PostgreSQL connection user
/// is passed here (indexed options like pg1-user cannot be set via environment variables) and
/// PGPASSWORD is exported as a fallback for non-peer authentication.
/// </summary>
internal class PgBackRestRunner(BackupServiceOptions options, ILogger<PgBackRestRunner> logger)
{
    public const string TypeFull = "full";
    public const string TypeDiff = "diff";

    public virtual async Task<PgBackRestStanzaInfo?> InfoAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "pgbackrest",
            ["--stanza=" + options.Stanza, "--log-level-console=error", "info", "--output=json"],
            throwOnError: true,
            cancellationToken: cancellationToken);
        return PgBackRestInfoParser.Parse(result.StandardOutput, options.Stanza);
    }

    /// <summary>
    /// Creates the stanza when the repository has none yet, then runs `check` to prove the
    /// archive_command round-trip works. Check failures are logged, not thrown: postgres may
    /// simply not be up yet (e.g. standalone restore mode).
    /// </summary>
    public virtual async Task EnsureStanzaAsync(CancellationToken cancellationToken)
    {
        var info = await InfoAsync(cancellationToken);
        if (info is null || info.Status?.Code == 1)
        {
            logger.LogInformation("Creating pgBackRest stanza '{Stanza}'.", options.Stanza);
            await ProcessRunner.RunAsync(
                "pgbackrest",
                [.. ConnectionArguments(), "stanza-create"],
                environment: ConnectionEnvironment(),
                throwOnError: true,
                cancellationToken: cancellationToken);
        }

        var check = await ProcessRunner.RunAsync(
            "pgbackrest",
            [.. ConnectionArguments(), "check"],
            environment: ConnectionEnvironment(),
            throwOnError: false,
            cancellationToken: cancellationToken);
        if (check.ExitCode != 0)
        {
            logger.LogWarning(
                "pgbackrest check failed (exit {ExitCode}): {Error}", check.ExitCode, Tail(check));
        }
    }

    /// <summary>Runs a backup and returns the label of the backup it produced.</summary>
    public virtual async Task<string> BackupAsync(
        string type,
        string name,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (type is not (TypeFull or TypeDiff))
            throw new ArgumentException($"Unsupported backup type '{type}'.", nameof(type));

        await RunStreamingAsync(
            [.. ConnectionArguments(), "backup", "--type=" + type, $"--annotation=name={name}"],
            progress,
            cancellationToken);

        var info = await InfoAsync(cancellationToken)
                   ?? throw new InvalidOperationException("pgbackrest info returned no stanza after the backup.");
        // Backups are listed oldest-first; the one just produced is the newest.
        var label = info.Backup.LastOrDefault()?.Label;
        return label ?? throw new InvalidOperationException("Could not resolve the new backup's label from pgbackrest info.");
    }

    /// <summary>Repository-wide integrity verification (checksums of backup files and WAL).</summary>
    public virtual Task VerifyAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunStreamingAsync(["--stanza=" + options.Stanza, "verify"], progress, cancellationToken);

    /// <summary>
    /// Restores into <paramref name="pgDataPath"/>. With <paramref name="targetTime"/> set the
    /// restore recovers to that moment (PITR) and promotes; otherwise it recovers to the end of
    /// the selected backup (or of the archived WAL when no label is given).
    /// </summary>
    public virtual Task RestoreAsync(
        string? label,
        DateTimeOffset? targetTime,
        string pgDataPath,
        bool immediateTarget,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--stanza=" + options.Stanza,
            "restore",
            "--delta",
            "--pg1-path=" + pgDataPath
        };
        if (!string.IsNullOrWhiteSpace(label))
            arguments.Add("--set=" + label);
        if (targetTime is { } time)
        {
            arguments.Add("--type=time");
            arguments.Add("--target=" + time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss+00", CultureInfo.InvariantCulture));
            arguments.Add("--target-action=promote");
        }
        else if (immediateTarget)
        {
            // Consistency at the end of the base backup, minimal WAL replay — used by deep verify.
            arguments.Add("--type=immediate");
            arguments.Add("--target-action=promote");
        }

        return RunStreamingAsync(arguments, progress, cancellationToken);
    }

    private List<string> ConnectionArguments()
        => ["--stanza=" + options.Stanza, "--pg1-user=" + options.PostgresUser];

    private Dictionary<string, string>? ConnectionEnvironment()
        => string.IsNullOrEmpty(options.PostgresPassword)
            ? null
            : new Dictionary<string, string> { ["PGPASSWORD"] = options.PostgresPassword };

    /// <summary>Runs pgbackrest forwarding each output line to <paramref name="progress"/>; throws on non-zero exit.</summary>
    private async Task RunStreamingAsync(
        IReadOnlyList<string> arguments,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pgbackrest",
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (ConnectionEnvironment() is { } environment)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        logger.LogInformation("Running pgbackrest {Arguments}", string.Join(' ', arguments));
        using var process = new Process { StartInfo = startInfo };
        var lastLines = new Queue<string>();
        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            progress.Report(line);
            lastLines.Enqueue(line);
            while (lastLines.Count > 20)
                lastLines.Dequeue();
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        if (!process.Start())
            throw new InvalidOperationException("Failed to start pgbackrest.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pgbackrest {arguments.FirstOrDefault(a => !a.StartsWith('-'))} failed with exit code {process.ExitCode}: "
                + string.Join(" | ", lastLines));
        }
    }

    private static string Tail(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return text.Length > 500 ? text[^500..] : text;
    }
}
