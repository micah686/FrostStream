namespace FrostStream.Lite;

public enum LiteCommand
{
    Run,
    Initialize,
    Backup,
    Restore
}

public static class LiteCommandLine
{
    public static LiteCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            return LiteCommand.Run;

        var command = args[0];
        return command.ToLowerInvariant() switch
        {
            "run" => LiteCommand.Run,
            "initialize" => LiteCommand.Initialize,
            "backup" => LiteCommand.Backup,
            "restore" => LiteCommand.Restore,
            _ => throw new ArgumentException(
                $"Unknown FrostStream Lite command '{command}'. Supported commands: run, initialize, backup, restore.")
        };
    }
}
