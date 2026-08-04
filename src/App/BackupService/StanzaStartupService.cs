using BackupService.PgBackRest;

namespace BackupService;

/// <summary>
/// Creates the pgBackRest stanza (and runs an archiving check) shortly after startup so the
/// postgres server's archive_command starts succeeding on a fresh repository. Retries quietly
/// and never crashes the host: in standalone restore mode postgres (and the repo's stanza)
/// may legitimately be unavailable, and the wizard must still come up.
/// </summary>
internal sealed class StanzaStartupService(
    PgBackRestRunner runner,
    ILogger<StanzaStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= 30 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await runner.EnsureStanzaAsync(stoppingToken);
                logger.LogInformation("pgBackRest stanza is ready.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "pgBackRest stanza setup attempt {Attempt} failed; retrying.", attempt);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
