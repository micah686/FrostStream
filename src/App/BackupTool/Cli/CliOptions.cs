namespace BackupTool;

internal sealed class CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public bool Has(string name) => _values.ContainsKey(name);

    public string Required(string name)
        => Get(name) ?? throw new InvalidOperationException($"Missing required option --{name}.");

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var result = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._values[name] = args[++i];
            }
            else
            {
                result._values[name] = "true";
            }
        }

        return result;
    }
}
