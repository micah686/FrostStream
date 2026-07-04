using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Backups;

namespace DataBridge.Backup;

public sealed class BackupRunner(
    IOptions<BackupRunnerOptions> options,
    IConfiguration configuration,
    ILogger<BackupRunner> logger)
{
    public async Task<(bool Success, string? ArchivePath, string? ErrorMessage)> RunAsync(
        string name,
        CancellationToken cancellationToken)
        => await RunAsync(name, mode: null, cancellationToken);

    public async Task<(bool Success, string? ArchivePath, string? ErrorMessage)> RunAsync(
        string name,
        string? mode,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(options.Value.Directory);

            var client = new BackupToolClient(options.Value, configuration);
            var commandArguments = new List<string> { "create", "--output", options.Value.Directory, "--name", name };
            if (!string.IsNullOrWhiteSpace(mode))
            {
                commandArguments.Add("--mode");
                commandArguments.Add(mode);
            }

            var output = await client.RunAsync(commandArguments, cancellationToken);
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
}
