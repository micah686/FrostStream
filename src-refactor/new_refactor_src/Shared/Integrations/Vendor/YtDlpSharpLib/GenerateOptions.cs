using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

return await OptionGenerationProgram.RunAsync(args).ConfigureAwait(false);

internal static class OptionGenerationProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CommandLineOptions.Usage);
                return 0;
            }

            var helpText = await ReadHelpTextAsync(options).ConfigureAwait(false);
            var sections = HelpParser.Parse(helpText);
            var files = CSharpEmitter.Emit(sections);

            return await OutputWriter.WriteOrCheckAsync(options, files).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<string> ReadHelpTextAsync(CommandLineOptions options)
    {
        if (options.HelpFile is not null)
        {
            return await File.ReadAllTextAsync(options.HelpFile).ConfigureAwait(false);
        }

        using var process = new Process();
        process.StartInfo.FileName = options.YtDlpPath;
        process.StartInfo.ArgumentList.Add("--help");
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start yt-dlp executable '{options.YtDlpPath}'.");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{options.YtDlpPath} --help' exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}

internal sealed record CommandLineOptions(
    string YtDlpPath,
    string OutputDir,
    bool DryRun,
    bool Check,
    string? HelpFile,
    bool ShowHelp)
{
    public const string DefaultOutputDir = "YtDlpSharpLib/Options/Generated";

    public const string Usage = """
        Usage:
          dotnet run GenerateOptions.cs -- [options]

        Options:
          --yt-dlp PATH       yt-dlp executable to inspect. Defaults to 'yt-dlp' on PATH.
          --output-dir DIR    Directory for generated .g.cs files. Defaults to YtDlpSharpLib/Options/Generated.
          --help-file FILE    Parse a captured yt-dlp --help fixture instead of running yt-dlp.
          --dry-run           Parse and report generated files without writing them.
          --check             Fail if generated files differ from the output directory.
          --help              Show this help text.
        """;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var ytDlpPath = "yt-dlp";
        var outputDir = DefaultOutputDir;
        var dryRun = false;
        var check = false;
        string? helpFile = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--yt-dlp":
                    ytDlpPath = RequireValue(args, ref i, "--yt-dlp");
                    break;
                case "--output-dir":
                    outputDir = RequireValue(args, ref i, "--output-dir");
                    break;
                case "--help-file":
                    helpFile = RequireValue(args, ref i, "--help-file");
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--check":
                    check = true;
                    break;
                case "-h":
                case "--help":
                    return new CommandLineOptions(ytDlpPath, outputDir, dryRun, check, helpFile, ShowHelp: true);
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return new CommandLineOptions(ytDlpPath, outputDir, dryRun, check, helpFile, ShowHelp: false);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }
}

internal static class HelpParser
{
    private static readonly Regex SectionRegex = new(@"^  (?<name>\S.*):\s*$", RegexOptions.Compiled);
    private static readonly Regex SpecDescriptionRegex = new(@"^(?<spec>.+?)(?:\s{2,}(?<description>\S.*))?$", RegexOptions.Compiled);
    private static readonly Regex FlagRegex = new(@"(?<!\S)-{1,2}[A-Za-z0-9][A-Za-z0-9-]*", RegexOptions.Compiled);
    private static readonly Regex AliasRegex = new(@"\(Alias(?:es)?: (?<aliases>[^)]*)\)", RegexOptions.Compiled);

    public static IReadOnlyList<HelpSection> Parse(string helpText)
    {
        ArgumentNullException.ThrowIfNull(helpText);

        var sections = new List<HelpSectionBuilder>();
        HelpSectionBuilder? currentSection = null;
        HelpOptionBuilder? currentOption = null;

        foreach (var rawLine in helpText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var sectionMatch = SectionRegex.Match(line);
            if (sectionMatch.Success)
            {
                currentOption = null;
                var sectionName = sectionMatch.Groups["name"].Value;
                if (sectionName.Equals("Preset Aliases", StringComparison.Ordinal))
                {
                    currentSection = null;
                    continue;
                }

                currentSection = new HelpSectionBuilder(sectionName);
                sections.Add(currentSection);
                continue;
            }

            if (currentSection is null)
            {
                continue;
            }

            if (line.StartsWith("    -", StringComparison.Ordinal))
            {
                var option = ParseOptionLine(line);
                if (option is null)
                {
                    currentOption = null;
                    continue;
                }

                currentOption = option;
                currentSection.Options.Add(option);
                continue;
            }

            if (currentOption is not null)
            {
                var text = line.Trim();
                if (text.Length > 0)
                {
                    currentOption.AppendDescription(text);
                }
            }
        }

        return sections
            .Select(section => section.ToImmutable())
            .Where(section => section.Options.Count > 0)
            .ToArray();
    }

    private static HelpOptionBuilder? ParseOptionLine(string line)
    {
        var content = line.Length > 4 ? line[4..] : line;
        var match = SpecDescriptionRegex.Match(content);
        if (!match.Success)
        {
            return null;
        }

        var spec = match.Groups["spec"].Value.Trim();
        var description = match.Groups["description"].Success
            ? match.Groups["description"].Value.Trim()
            : string.Empty;

        var flagMatches = FlagRegex.Matches(spec);
        var canonicalMatch = flagMatches.FirstOrDefault(static flag => flag.Value.StartsWith("--", StringComparison.Ordinal));
        if (canonicalMatch is null)
        {
            return null;
        }

        var aliases = flagMatches
            .Where(flag => flag.Index != canonicalMatch.Index || flag.Length != canonicalMatch.Length)
            .Select(flag => flag.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var valueName = spec[(canonicalMatch.Index + canonicalMatch.Length)..].Trim();
        return new HelpOptionBuilder(canonicalMatch.Value, aliases, valueName, description);
    }

    public static IReadOnlyList<string> ExtractAliases(string description)
    {
        var aliases = new List<string>();
        foreach (Match match in AliasRegex.Matches(description))
        {
            aliases.AddRange(FlagRegex.Matches(match.Groups["aliases"].Value).Select(flag => flag.Value));
        }

        return aliases.Distinct(StringComparer.Ordinal).ToArray();
    }

    private sealed class HelpSectionBuilder(string name)
    {
        public string Name { get; } = name;

        public List<HelpOptionBuilder> Options { get; } = [];

        public HelpSection ToImmutable() => new(Name, Options.Select(static option => option.ToImmutable()).ToArray());
    }

    private sealed class HelpOptionBuilder(
        string flag,
        IReadOnlyList<string> aliases,
        string valueName,
        string initialDescription)
    {
        private readonly StringBuilder _description = new();

        public string Flag { get; } = flag;

        public IReadOnlyList<string> Aliases { get; } = aliases;

        public string ValueName { get; } = valueName;

        public void AppendDescription(string text)
        {
            if (_description.Length > 0)
            {
                if (_description[^1] != '-')
                {
                    _description.Append(' ');
                }
            }

            _description.Append(text);
        }

        public HelpOption ToImmutable()
        {
            if (initialDescription.Length > 0 && _description.Length == 0)
            {
                AppendDescription(initialDescription);
            }
            else if (initialDescription.Length > 0)
            {
                var continuation = _description.ToString();
                _description.Clear();
                AppendDescription(initialDescription);
                AppendDescription(continuation);
            }

            var description = _description.ToString();
            var aliases = Aliases
                .Concat(HelpParser.ExtractAliases(description))
                .Where(alias => !alias.Equals(Flag, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            return new HelpOption(Flag, aliases, ValueName, description);
        }
    }
}

internal sealed record HelpSection(string Name, IReadOnlyList<HelpOption> Options);

internal sealed record HelpOption(
    string Flag,
    IReadOnlyList<string> Aliases,
    string ValueName,
    string Description);

internal static class CSharpEmitter
{
    private const string Header = """
        // <auto-generated/>
        // Generated by GenerateOptions.cs from yt-dlp --help.
        #nullable enable

        """;

    private static readonly Regex ParenthesesRegex = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex QuotedValueRegex = new("\"(?<value>[A-Za-z0-9_:+.-]+)\"", RegexOptions.Compiled);
    private static readonly Regex SupportedValueRegex = new(@"(?<value>[a-z][a-z0-9_:+.-]*)(?:\s*\(|,|$)", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string> ManualOptionTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--audio-format"] = "AudioConversionFormat",
            ["--convert-subs"] = "SubtitleFormat",
            ["--merge-output-format"] = "DownloadMergeFormat",
            ["--recode-video"] = "VideoRecodeFormat",
            ["--remux-video"] = "VideoContainer",
            ["--sub-format"] = "SubtitleFormat"
        };

    public static IReadOnlyDictionary<string, string> Emit(IReadOnlyList<HelpSection> sections)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var groups = sections.Select((section, index) => GroupModel.FromSection(section, index)).ToArray();

        files["YtDlpOptions.Generated.g.cs"] = EmitRoot(groups);
        files["YtDlpDeprecatedOptions.g.cs"] = EmitDeprecatedGroup();

        foreach (var group in groups)
        {
            files[$"{group.TypeName}.g.cs"] = EmitGroup(group);
        }

        return new ReadOnlyDictionary<string, string>(files);
    }

    private static string EmitRoot(IReadOnlyList<GroupModel> groups)
    {
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.AppendLine("namespace YtDlpSharpLib.Options;");
        sb.AppendLine();
        sb.AppendLine("public sealed partial record YtDlpOptions");
        sb.AppendLine("{");

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            sb.AppendLine($"    /// <summary>Generated options from yt-dlp's {EscapeXml(group.SectionName)} section.</summary>");
            sb.AppendLine($"    [YtDlpOptionGroup({100 + i})]");
            sb.AppendLine($"    public {group.TypeName} {group.PropertyName} {{ get; init; }} = new();");

            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>Generated deprecated legacy options for older youtube-dl/yt-dlp config compatibility.</summary>");
        sb.AppendLine("    [YtDlpOptionGroup(200)]");
        sb.AppendLine("    public YtDlpDeprecatedOptions Deprecated { get; init; } = new();");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EmitGroup(GroupModel group)
    {
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.AppendLine("namespace YtDlpSharpLib.Options;");
        sb.AppendLine();
        sb.AppendLine($"public sealed record {group.TypeName}");
        sb.AppendLine("{");

        for (var i = 0; i < group.Options.Count; i++)
        {
            var option = group.Options[i];
            sb.AppendLine($"    /// <summary>{EscapeXml(option.DescriptionForDoc)}</summary>");
            sb.AppendLine($"    [{BuildAttribute(option)}]");
            sb.AppendLine($"    public {option.PropertyType} {option.PropertyName} {{ get; init; }}{option.Initializer}");

            if (i + 1 < group.Options.Count)
            {
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");

        foreach (var enumModel in group.Enums)
        {
            sb.AppendLine();
            sb.AppendLine($"public enum {enumModel.TypeName}");
            sb.AppendLine("{");
            for (var i = 0; i < enumModel.Values.Count; i++)
            {
                var value = enumModel.Values[i];
                sb.AppendLine($"    [YtDlpEnumValue({ToLiteral(value.Token)})]");
                sb.Append($"    {value.MemberName}");
                sb.AppendLine(i + 1 < enumModel.Values.Count ? "," : string.Empty);
                if (i + 1 < enumModel.Values.Count)
                {
                    sb.AppendLine();
                }
            }

            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static string EmitDeprecatedGroup()
    {
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.AppendLine("namespace YtDlpSharpLib.Options;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Generated compatibility surface for legacy youtube-dl/yt-dlp flags that no longer appear in current help output.");
        sb.AppendLine("/// Prefer the non-deprecated replacement options for new code.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public sealed record YtDlpDeprecatedOptions");
        sb.AppendLine("{");

        for (var i = 0; i < DeprecatedOptions.Count; i++)
        {
            var option = DeprecatedOptions[i];
            sb.AppendLine($"    /// <summary>{EscapeXml(option.Description)}</summary>");
            sb.AppendLine($"    [Obsolete({ToLiteral(option.ObsoleteMessage)})]");
            sb.AppendLine($"    [{BuildDeprecatedAttribute(option)}]");
            sb.AppendLine($"    public {option.PropertyType} {option.PropertyName} {{ get; init; }}");

            if (i + 1 < DeprecatedOptions.Count)
            {
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildAttribute(OptionModel option)
    {
        var arguments = new List<string> { ToLiteral(option.Flag) };

        if (option.IsSwitch)
        {
            arguments.Add("ValueStyle = ArgumentValueStyle.Switch");
        }

        if (option.AllowMultiple)
        {
            arguments.Add("AllowMultiple = true");
        }

        if (option.ValueTokenCount > 1)
        {
            arguments.Add($"ValueTokenCount = {option.ValueTokenCount.ToString(CultureInfo.InvariantCulture)}");
        }

        if (option.Aliases.Count > 0)
        {
            arguments.Add($"Aliases = new[] {{ {string.Join(", ", option.Aliases.Select(ToLiteral))} }}");
        }

        if (option.ValueName.Length > 0)
        {
            arguments.Add($"ValueName = {ToLiteral(option.ValueName)}");
        }

        if (option.Description.Length > 0)
        {
            arguments.Add($"Description = {ToLiteral(option.Description)}");
        }

        arguments.Add("IsGenerated = true");
        return $"YtDlpArgument({string.Join(", ", arguments)})";
    }

    private static string BuildDeprecatedAttribute(DeprecatedOptionModel option)
    {
        var arguments = new List<string> { ToLiteral(option.Flag) };

        if (option.IsSwitch)
        {
            arguments.Add("ValueStyle = ArgumentValueStyle.Switch");
        }

        if (option.ValueName.Length > 0)
        {
            arguments.Add($"ValueName = {ToLiteral(option.ValueName)}");
        }

        arguments.Add($"Description = {ToLiteral(option.Description)}");
        arguments.Add("IsGenerated = true");
        return $"YtDlpArgument({string.Join(", ", arguments)})";
    }

    private static OptionType InferType(GroupModel group, HelpOption option, string propertyName)
    {
        var valueName = option.ValueName.Trim();
        var valueTokenCount = CountValueTokens(valueName);
        var repeated = IsRepeated(option.Description);
        if (valueName.Length == 0)
        {
            return new OptionType("bool", string.Empty, true, repeated, 0, null);
        }

        if (TryGetManualOptionType(option, repeated, valueTokenCount, out var manualType))
        {
            return manualType;
        }

        if (valueTokenCount > 1)
        {
            var typeName = repeated ? "IReadOnlyList<IReadOnlyList<string>>" : "IReadOnlyList<string>?";
            var initializer = repeated ? " = [];" : string.Empty;
            return new OptionType(typeName, initializer, false, repeated, valueTokenCount, null);
        }

        if (TryCreateEnum(group, option, propertyName, out var enumModel))
        {
            var typeName = repeated ? $"IReadOnlyList<{enumModel.TypeName}>" : $"{enumModel.TypeName}?";
            var initializer = repeated ? " = [];" : string.Empty;
            return new OptionType(typeName, initializer, false, repeated, valueTokenCount, enumModel);
        }

        var scalarType = InferScalarType(valueName, option.Description);
        if (repeated)
        {
            return new OptionType($"IReadOnlyList<{scalarType}>", " = [];", false, repeated, valueTokenCount, null);
        }

        return new OptionType($"{scalarType}?", string.Empty, false, repeated, valueTokenCount, null);
    }

    private static bool TryGetManualOptionType(
        HelpOption option,
        bool repeated,
        int valueTokenCount,
        out OptionType optionType)
    {
        optionType = new OptionType(string.Empty, string.Empty, false, false, 0, null);

        if (!ManualOptionTypes.TryGetValue(option.Flag, out var typeName) || valueTokenCount != 1)
        {
            return false;
        }

        optionType = new OptionType(
            repeated ? $"IReadOnlyList<{typeName}>" : $"{typeName}?",
            repeated ? " = [];" : string.Empty,
            false,
            repeated,
            valueTokenCount,
            null);
        return true;
    }

    private static int CountValueTokens(string valueName) =>
        valueName.Length == 0 ? 0 : valueName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool IsRepeated(string description) =>
        description.Contains("used multiple times", StringComparison.OrdinalIgnoreCase)
        || description.Contains("use this option multiple times", StringComparison.OrdinalIgnoreCase)
        || description.Contains("option can be used multiple times", StringComparison.OrdinalIgnoreCase);

    private static string InferScalarType(string valueName, string description)
    {
        var normalized = valueName.Trim().Trim('[', ']').ToUpperInvariant();
        if (description.Contains(" or \"infinite\"", StringComparison.OrdinalIgnoreCase)
            || description.Contains(" e.g. ", StringComparison.OrdinalIgnoreCase)
            || valueName.Contains(':', StringComparison.Ordinal)
            || valueName.Contains('-', StringComparison.Ordinal)
            || valueName.Contains('@', StringComparison.Ordinal)
            || valueName.Contains('/', StringComparison.Ordinal))
        {
            return "string";
        }

        return normalized switch
        {
            "SECONDS" => "double",
            "N" => "int",
            "NUMBER" => "int",
            "YEARS" => "int",
            "LENGTH" => "int",
            _ => "string"
        };
    }

    private static bool TryCreateEnum(
        GroupModel group,
        HelpOption option,
        string propertyName,
        out EnumModel enumModel)
    {
        enumModel = new EnumModel(string.Empty, []);

        if (option.ValueName.Contains(':', StringComparison.Ordinal)
            || option.ValueName.Contains('[', StringComparison.Ordinal)
            || option.ValueName.Contains('/', StringComparison.Ordinal)
            || option.ValueName.Contains('@', StringComparison.Ordinal)
            || option.Description.Contains("CIDR", StringComparison.OrdinalIgnoreCase)
            || option.Description.Contains("separated by commas", StringComparison.OrdinalIgnoreCase)
            || option.Description.Contains("multiple rules", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = ExtractClosedValues(option.Description);
        if (values.Count < 2)
        {
            return false;
        }

        var enumValues = new List<EnumValueModel>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var memberName = ToPascalIdentifier(value);
            if (!usedNames.Add(memberName))
            {
                var suffix = 2;
                while (!usedNames.Add(memberName + suffix.ToString(CultureInfo.InvariantCulture)))
                {
                    suffix++;
                }

                memberName += suffix.ToString(CultureInfo.InvariantCulture);
            }

            enumValues.Add(new EnumValueModel(memberName, value));
        }

        enumModel = new EnumModel($"YtDlp{group.BaseName}{propertyName}", enumValues);
        return true;
    }

    private static IReadOnlyList<string> ExtractClosedValues(string description)
    {
        if (!description.Contains("One of", StringComparison.OrdinalIgnoreCase)
            && !description.Contains("Supported values:", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var oneOfIndex = CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            description,
            "One of",
            CompareOptions.IgnoreCase);
        if (oneOfIndex >= 0)
        {
            var fragment = description[(oneOfIndex + "One of".Length)..];
            var end = fragment.IndexOf('.', StringComparison.Ordinal);
            if (end >= 0)
            {
                fragment = fragment[..end];
            }

            var quoted = QuotedValueRegex.Matches(fragment)
                .Select(static match => match.Groups["value"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (quoted.Length >= 2)
            {
                return quoted;
            }

            fragment = ParenthesesRegex.Replace(fragment, string.Empty)
                .Replace(" or ", ",", StringComparison.OrdinalIgnoreCase)
                .Replace(" and ", ",", StringComparison.OrdinalIgnoreCase);

            return fragment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => value.Trim('"', '\'', '`', ' '))
                .Where(static value => Regex.IsMatch(value, "^[a-z][a-z0-9_:+.-]*$", RegexOptions.IgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var supportedIndex = CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            description,
            "Supported values:",
            CompareOptions.IgnoreCase);
        if (supportedIndex >= 0)
        {
            var fragment = description[(supportedIndex + "Supported values:".Length)..];
            var end = fragment.IndexOf('.', StringComparison.Ordinal);
            if (end >= 0)
            {
                fragment = fragment[..end];
            }

            var quoted = QuotedValueRegex.Matches(fragment)
                .Select(static match => match.Groups["value"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (quoted.Length >= 2)
            {
                return quoted;
            }

            return SupportedValueRegex.Matches(fragment)
                .Select(static match => match.Groups["value"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return [];
    }

    private static string ToLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            _ = ch switch
            {
                '\\' => sb.Append(@"\\"),
                '"' => sb.Append("\\\""),
                '\r' => sb.Append(@"\r"),
                '\n' => sb.Append(@"\n"),
                '\t' => sb.Append(@"\t"),
                _ => sb.Append(ch)
            };
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string ToPascalIdentifier(string value)
    {
        var parts = Regex.Split(value, "[^A-Za-z0-9]+")
            .Where(static part => part.Length > 0)
            .ToArray();

        var identifier = string.Concat(parts.Select(static part =>
            char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

        if (identifier.Length == 0)
        {
            identifier = "Value";
        }

        if (char.IsDigit(identifier[0]))
        {
            identifier = "Value" + identifier;
        }

        return identifier;
    }

    private sealed record GroupModel(
        string SectionName,
        string BaseName,
        string TypeName,
        string PropertyName,
        IReadOnlyList<OptionModel> Options,
        IReadOnlyList<EnumModel> Enums)
    {
        public static GroupModel FromSection(HelpSection section, int index)
        {
            var baseName = ToGroupBaseName(section.Name);
            var usedPropertyNames = new HashSet<string>(StringComparer.Ordinal);
            var options = new List<OptionModel>();
            var enums = new List<EnumModel>();
            var temporaryGroup = new GroupModel(
                section.Name,
                baseName,
                $"YtDlp{baseName}Options",
                baseName,
                [],
                []);

            foreach (var helpOption in section.Options)
            {
                var propertyName = ToUniquePropertyName(ToPascalIdentifier(helpOption.Flag[2..]), usedPropertyNames);
                var optionType = InferType(temporaryGroup, helpOption, propertyName);
                if (optionType.EnumModel is not null)
                {
                    enums.Add(optionType.EnumModel);
                }

                options.Add(new OptionModel(
                    helpOption.Flag,
                    helpOption.Aliases,
                    helpOption.ValueName,
                    helpOption.Description,
                    propertyName,
                    optionType.TypeName,
                    optionType.Initializer,
                    optionType.IsSwitch,
                    optionType.AllowMultiple,
                    optionType.ValueTokenCount));
            }

            return temporaryGroup with
            {
                Options = options,
                Enums = enums,
                PropertyName = ToUniqueGroupPropertyName(baseName, index)
            };
        }

        private static string ToGroupBaseName(string sectionName)
        {
            var name = sectionName;
            if (name.EndsWith(" Options", StringComparison.Ordinal))
            {
                name = name[..^" Options".Length];
            }

            name = name.Replace(" and ", " ", StringComparison.OrdinalIgnoreCase);
            var baseName = ToPascalIdentifier(name);
            return baseName.Equals("Sponsorblock", StringComparison.Ordinal) ? "SponsorBlock" : baseName;
        }

        private static string ToUniqueGroupPropertyName(string baseName, int index) =>
            baseName.Length == 0 ? $"Group{index.ToString(CultureInfo.InvariantCulture)}" : baseName;

        private static string ToUniquePropertyName(string propertyName, HashSet<string> usedNames)
        {
            if (usedNames.Add(propertyName))
            {
                return propertyName;
            }

            var suffix = 2;
            while (!usedNames.Add(propertyName + suffix.ToString(CultureInfo.InvariantCulture)))
            {
                suffix++;
            }

            return propertyName + suffix.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed record OptionModel(
        string Flag,
        IReadOnlyList<string> Aliases,
        string ValueName,
        string Description,
        string PropertyName,
        string PropertyType,
        string Initializer,
        bool IsSwitch,
        bool AllowMultiple,
        int ValueTokenCount)
    {
        public string DescriptionForDoc =>
            Description.Length == 0 ? $"Maps to <c>{EscapeXml(Flag)}</c>." : Description;
    }

    private sealed record OptionType(
        string TypeName,
        string Initializer,
        bool IsSwitch,
        bool AllowMultiple,
        int ValueTokenCount,
        EnumModel? EnumModel);

    private sealed record EnumModel(string TypeName, IReadOnlyList<EnumValueModel> Values);

    private sealed record EnumValueModel(string MemberName, string Token);

    private sealed record DeprecatedOptionModel(
        string Flag,
        string PropertyName,
        string PropertyType,
        bool IsSwitch,
        string ValueName,
        string Description,
        string ObsoleteMessage);

    private static readonly IReadOnlyList<DeprecatedOptionModel> DeprecatedOptions =
    [
        new(
            "--match-title",
            "MatchTitle",
            "string?",
            IsSwitch: false,
            "REGEX",
            "Deprecated legacy title include filter.",
            "Use VideoSelection.MatchFilters instead."),
        new(
            "--reject-title",
            "RejectTitle",
            "string?",
            IsSwitch: false,
            "REGEX",
            "Deprecated legacy title exclude filter.",
            "Use VideoSelection.MatchFilters instead."),
        new(
            "--metadata-from-title",
            "MetadataFromTitle",
            "string?",
            IsSwitch: false,
            "FORMAT",
            "Deprecated legacy metadata parsing shortcut.",
            "Use PostProcessing.ParseMetadata instead."),
        new(
            "--hls-prefer-native",
            "HlsPreferNative",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy native HLS downloader selector.",
            "Use Download.Downloader with an m3u8/native selector instead."),
        new(
            "--hls-prefer-ffmpeg",
            "HlsPreferFfmpeg",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy FFmpeg HLS downloader selector.",
            "Use Download.Downloader with an m3u8/ffmpeg selector instead."),
        new(
            "--prefer-avconv",
            "PreferAvconv",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy avconv preference flag.",
            "Use PostProcessing.FfmpegLocation or yt-dlp compatibility options instead."),
        new(
            "--prefer-ffmpeg",
            "PreferFfmpeg",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy FFmpeg preference flag.",
            "Use PostProcessing.FfmpegLocation or yt-dlp compatibility options instead."),
        new(
            "--avconv-location",
            "AvconvLocation",
            "string?",
            IsSwitch: false,
            "PATH",
            "Deprecated legacy avconv binary path.",
            "Use PostProcessing.FfmpegLocation instead."),
        new(
            "--cn-verification-proxy",
            "CnVerificationProxy",
            "string?",
            IsSwitch: false,
            "URL",
            "Deprecated legacy China verification proxy option.",
            "Use GeoRestriction.GeoVerificationProxy instead."),
        new(
            "--youtube-skip-dash-manifest",
            "YoutubeSkipDashManifest",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy YouTube DASH manifest skip flag.",
            "Use Extractor.ExtractorArgs for YouTube extractor-specific behavior instead."),
        new(
            "--write-annotations",
            "WriteAnnotations",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy YouTube annotations sidecar flag.",
            "YouTube annotations are no longer available."),
        new(
            "--load-pages",
            "LoadPages",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy intermediate page dump flag.",
            "Use VerbositySimulation.DumpPages or VerbositySimulation.WritePages instead."),
        new(
            "--no-call-home",
            "NoCallHome",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy youtube-dl telemetry opt-out flag.",
            "yt-dlp does not use the legacy call-home behavior."),
        new(
            "--include-ads",
            "IncludeAds",
            "bool",
            IsSwitch: true,
            string.Empty,
            "Deprecated legacy ad download flag.",
            "This legacy youtube-dl option has no supported yt-dlp replacement."),
        new(
            "--autonumber-size",
            "AutonumberSize",
            "int?",
            IsSwitch: false,
            "NUMBER",
            "Deprecated legacy autonumber width option.",
            "Use numeric formatting in Filesystem.Output instead.")
    ];
}

internal static class OutputWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> WriteOrCheckAsync(
        CommandLineOptions options,
        IReadOnlyDictionary<string, string> generatedFiles)
    {
        if (options.Check)
        {
            return await CheckAsync(options.OutputDir, generatedFiles).ConfigureAwait(false);
        }

        if (options.DryRun)
        {
            Console.WriteLine($"Would write {generatedFiles.Count} files to {options.OutputDir}:");
            foreach (var fileName in generatedFiles.Keys)
            {
                Console.WriteLine($"  {fileName}");
            }

            return 0;
        }

        Directory.CreateDirectory(options.OutputDir);
        foreach (var file in generatedFiles)
        {
            var path = Path.Combine(options.OutputDir, file.Key);
            await File.WriteAllTextAsync(path, file.Value, Utf8NoBom).ConfigureAwait(false);
        }

        foreach (var staleFile in Directory.EnumerateFiles(options.OutputDir, "*.g.cs")
                     .Select(Path.GetFileName)
                     .Where(fileName => fileName is not null && !generatedFiles.ContainsKey(fileName))
                     .Select(fileName => Path.Combine(options.OutputDir, fileName!)))
        {
            File.Delete(staleFile);
        }

        Console.WriteLine($"Wrote {generatedFiles.Count} files to {options.OutputDir}.");
        return 0;
    }

    private static async Task<int> CheckAsync(string outputDir, IReadOnlyDictionary<string, string> generatedFiles)
    {
        var existingFiles = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir, "*.g.cs")
                .Select(path => new { FileName = Path.GetFileName(path), Contents = File.ReadAllText(path) })
                .Where(file => file.FileName is not null)
                .ToDictionary(file => file.FileName!, file => file.Contents, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var missing = generatedFiles.Keys.Except(existingFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = existingFiles.Keys.Except(generatedFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = generatedFiles.Keys
            .Intersect(existingFiles.Keys, StringComparer.Ordinal)
            .Where(fileName => !string.Equals(generatedFiles[fileName], existingFiles[fileName], StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0 && extra.Length == 0 && changed.Length == 0)
        {
            Console.WriteLine("Generated option files are up to date.");
            return 0;
        }

        WriteDifferences("Missing", missing);
        WriteDifferences("Extra", extra);
        WriteDifferences("Changed", changed);
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    private static void WriteDifferences(string label, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine($"{label} generated files:");
        foreach (var file in files)
        {
            Console.Error.WriteLine($"  {file}");
        }
    }
}
