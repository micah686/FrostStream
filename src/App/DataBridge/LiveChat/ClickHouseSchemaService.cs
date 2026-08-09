using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataBridge.LiveChat;

/// <summary>
/// Applies the versioned ClickHouse DDL scripts embedded under <c>LiveChat/Schema/</c>
/// (<c>NNN_name.sql</c>) at startup, tracked in a <c>schema_migrations</c> table. Registered
/// only when live chat is enabled; blocks host start until the schema is current so the ingest
/// and query consumers never race it. All DDL is written with <c>IF NOT EXISTS</c> as
/// belt-and-braces — re-running a script must be harmless.
/// </summary>
public sealed partial class ClickHouseSchemaService(
    ClickHouseAccess clickHouse,
    ILogger<ClickHouseSchemaService> logger) : IHostedService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApplySchemaAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Live chat is optional, so a broken ClickHouse must not take DataBridge down with
            // it — WebAPI waits on DataBridge, so throwing here would 502 the whole site. Chat
            // ingest and queries will fail loudly until it recovers; everything else runs.
            logger.LogError(ex,
                "ClickHouse schema setup failed. Live chat replay will be unavailable until ClickHouse " +
                "is reachable and DataBridge is restarted; the rest of FrostStream is unaffected.");
        }
    }

    private async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        await WaitForClickHouseAsync(cancellationToken);

        await using var connection = clickHouse.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    UInt32,
                    applied_at DateTime DEFAULT now()
                ) ENGINE = MergeTree ORDER BY version
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        uint currentVersion;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT toUInt32(max(version)) FROM schema_migrations";
            currentVersion = Convert.ToUInt32(await read.ExecuteScalarAsync(cancellationToken) ?? 0u);
        }

        foreach (var (version, name, sql) in LoadScripts().Where(s => s.Version > currentVersion))
        {
            logger.LogInformation("Applying ClickHouse schema migration {Version} ({Name})…", version, name);
            // The ClickHouse HTTP interface takes one statement per request.
            foreach (var statement in SplitStatements(sql))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var record = connection.CreateCommand();
            record.CommandText = $"INSERT INTO schema_migrations (version) VALUES ({version})";
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("ClickHouse live-chat schema is current (version {Version}).",
            LoadScripts().Select(s => s.Version).DefaultIfEmpty(currentVersion).Max());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WaitForClickHouseAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
        while (true)
        {
            try
            {
                await using var connection = clickHouse.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                logger.LogInformation(ex, "ClickHouse is not reachable yet; retrying in {Delay}s…",
                    ConnectRetryDelay.TotalSeconds);
                await Task.Delay(ConnectRetryDelay, cancellationToken);
            }
        }
    }

    private static IReadOnlyList<(uint Version, string Name, string Sql)> LoadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var scripts = new List<(uint, string, string)>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var match = ScriptResourceName().Match(resourceName);
            if (!match.Success)
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            scripts.Add((uint.Parse(match.Groups["version"].Value), match.Groups["name"].Value, reader.ReadToEnd()));
        }

        return scripts.OrderBy(s => s.Item1).ToArray();
    }

    private static IEnumerable<string> SplitStatements(string sql)
        => sql.Split(';')
            .Select(static statement => statement.Trim())
            .Where(static statement => statement.Length > 0 &&
                // A trailing chunk of only comment lines is not a statement.
                statement.Split('\n').Any(static line =>
                {
                    var trimmed = line.Trim();
                    return trimmed.Length > 0 && !trimmed.StartsWith("--", StringComparison.Ordinal);
                }));

    [GeneratedRegex(@"LiveChat\.Schema\.(?<version>\d+)_(?<name>\w+)\.sql$")]
    private static partial Regex ScriptResourceName();
}
