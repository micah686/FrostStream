namespace Shared.Backups;

public static class BackupTypes
{
    public const string Full = "full";
    public const string Differential = "diff";
    public const string Incremental = "incr";

    public static string Normalize(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or Full => Full,
        Differential or "differential" => Differential,
        Incremental or "incremental" => Incremental,
        _ => throw new NotSupportedException($"Unsupported backup type '{type}'. Use full, diff, or incr.")
    };

    public static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 128 || trimmed.Any(char.IsControl))
            throw new ArgumentException("Backup names must contain 1–128 printable characters.");

        return trimmed;
    }
}
