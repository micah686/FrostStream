namespace BackupService;

/// <summary>
/// Runs PostgreSQL client tools (pg_dump, pg_restore, pg_basebackup, …), resolving the tool
/// through the optional bin directory and injecting <c>PGPASSWORD</c> into the environment.
/// </summary>
internal sealed class PostgresToolRunner(PostgresOptions options)
{
    public async Task<string> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        bool allowFailure = false,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            options.ToolPath(tool),
            arguments,
            BuildEnvironment(),
            throwOnError: !allowFailure,
            cancellationToken);
        return result.StandardOutput;
    }

    private IReadOnlyDictionary<string, string>? BuildEnvironment()
        => string.IsNullOrEmpty(options.Password)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["PGPASSWORD"] = options.Password };
}
