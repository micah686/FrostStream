using System.Text;

namespace FrostStream.Deployment;

public static class DeploymentEnvironmentFiles
{
    public static IReadOnlyDictionary<string, string> Read(string? path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return values;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid environment assignment in '{Path.GetFileName(path)}'.");
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
            {
                value = value[1..^1];
            }

            values[name] = value;
        }

        return values;
    }

    public static void WriteResolved(string path, IReadOnlyDictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var builder = new StringBuilder();
        builder.AppendLine("# Generated deployment inputs. Keep this file private and do not commit it.");
        foreach (var pair in values.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').AppendLine(Quote(pair.Value));
        }

        File.WriteAllText(temporaryPath, builder.ToString());
        File.Move(temporaryPath, path, overwrite: true);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static IReadOnlyDictionary<string, string> SnapshotProcessEnvironment(
        IReadOnlySet<string> includedNames) =>
        Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string name && entry.Value is string && includedNames.Contains(name))
            .ToDictionary(
                static entry => (string)entry.Key,
                static entry => (string)entry.Value!,
                StringComparer.Ordinal);

    public static void ApplyMissing(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            if (Environment.GetEnvironmentVariable(pair.Key) is null)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(static c => char.IsWhiteSpace(c) || c is '#' or '"' or '\'')
            ? $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
}
