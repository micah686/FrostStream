using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib.Options;

internal static class YtDlpConfigFile
{
    private static readonly Lazy<IReadOnlyDictionary<string, OptionBinding>> OptionBindings = new(BuildOptionBindings);

    public static YtDlpOptions Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var options = new YtDlpOptions();
        var tokens = lines.SelectMany(TokenizeConfigLine).ToList();
        var advanced = new List<RawYtDlpArgument>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var equalsIndex = token.StartsWith("-", StringComparison.Ordinal)
                ? token.IndexOf('=', StringComparison.Ordinal)
                : -1;
            var optionName = equalsIndex > 0 ? token[..equalsIndex] : token;
            string? inlineValue = equalsIndex > 0 ? token[(equalsIndex + 1)..] : null;

            if (!optionName.StartsWith("-", StringComparison.Ordinal))
            {
                throw new YtDlpValidationException(
                    $"Config token '{optionName}' is not an option flag.");
            }

            if (!OptionBindings.Value.TryGetValue(optionName, out var binding))
            {
                i = AddAdvancedArgument(tokens, i, optionName, inlineValue, advanced);
                continue;
            }

            if (binding.Attribute.ValueStyle == ArgumentValueStyle.Switch)
            {
                if (inlineValue is not null)
                {
                    throw new YtDlpValidationException(
                        $"Switch argument '{optionName}' does not accept a value.");
                }

                var group = binding.GroupProperty.GetValue(options)
                    ?? throw new YtDlpValidationException($"Option group '{binding.GroupProperty.Name}' is null.");
                binding.OptionProperty.SetValue(group, true);
                continue;
            }

            var values = new string[binding.Attribute.ValueTokenCount];
            if (inlineValue is not null)
            {
                values[0] = inlineValue;
            }
            else
            {
                i++;
                if (i >= tokens.Count)
                {
                    throw new YtDlpValidationException(
                        $"Argument '{optionName}' expects {binding.Attribute.ValueTokenCount.ToString(CultureInfo.InvariantCulture)} value token(s).");
                }

                values[0] = tokens[i];
            }

            for (var valueIndex = 1; valueIndex < values.Length; valueIndex++)
            {
                i++;
                if (i >= tokens.Count)
                {
                    throw new YtDlpValidationException(
                        $"Argument '{optionName}' expects {binding.Attribute.ValueTokenCount.ToString(CultureInfo.InvariantCulture)} value token(s).");
                }

                values[valueIndex] = tokens[i];
            }

            SetParsedValue(options, binding, values);
        }

        if (advanced.Count > 0)
        {
            options = options with { AdvancedArguments = advanced };
        }

        return options;
    }

    public static IReadOnlyList<string> RenderArguments(YtDlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new YtDlpArgumentRenderer().Render(options);
    }

    public static IEnumerable<string> RenderConfigLines(YtDlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var args = RenderArguments(options with { AdvancedArguments = [] });

        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            if (!OptionBindings.Value.TryGetValue(flag, out var binding)
                || binding.Attribute.ValueStyle == ArgumentValueStyle.Switch)
            {
                yield return QuoteConfigToken(flag);
                continue;
            }

            var values = new string[binding.Attribute.ValueTokenCount];
            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                i++;
                if (i >= args.Count)
                {
                    throw new YtDlpValidationException(
                        $"Argument '{flag}' rendered without enough value tokens.");
                }

                values[valueIndex] = args[i];
            }

            yield return string.Join(' ', values.Prepend(flag).Select(QuoteConfigToken));
        }

        foreach (var argument in options.AdvancedArguments)
        {
            var values = new[] { argument.Value }
                .Concat(argument.Values)
                .Where(static value => value is not null)
                .Select(static value => value!);

            yield return string.Join(' ', values.Prepend(argument.Name).Select(QuoteConfigToken));
        }
    }

    public static string RenderCommandLine(YtDlpOptions options) =>
        string.Join(' ', RenderArguments(options).Select(QuoteCommandLineToken));

    private static int AddAdvancedArgument(
        IReadOnlyList<string> tokens,
        int index,
        string optionName,
        string? inlineValue,
        List<RawYtDlpArgument> advanced)
    {
        if (!optionName.StartsWith("--", StringComparison.Ordinal))
        {
            throw new YtDlpValidationException(
                $"Unknown short option '{optionName}' cannot be represented as a raw advanced argument.");
        }

        if (inlineValue is not null)
        {
            advanced.Add(new RawYtDlpArgument { Name = optionName, Value = inlineValue });
            return index;
        }

        if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            advanced.Add(new RawYtDlpArgument { Name = optionName, Value = tokens[index + 1] });
            return index + 1;
        }

        advanced.Add(new RawYtDlpArgument { Name = optionName });
        return index;
    }

    private static void SetParsedValue(YtDlpOptions options, OptionBinding binding, IReadOnlyList<string> values)
    {
        var group = binding.GroupProperty.GetValue(options)
            ?? throw new YtDlpValidationException($"Option group '{binding.GroupProperty.Name}' is null.");

        if (binding.Attribute.AllowMultiple)
        {
            AppendParsedValue(group, binding, values);
            return;
        }

        var parsed = binding.Attribute.ValueTokenCount == 1
            ? ConvertValue(values[0], binding.OptionProperty.PropertyType)
            : values.ToArray();
        binding.OptionProperty.SetValue(group, parsed);
    }

    private static void AppendParsedValue(object group, OptionBinding binding, IReadOnlyList<string> values)
    {
        var listType = binding.OptionProperty.PropertyType;
        if (!listType.IsGenericType
            || listType.GetGenericTypeDefinition() != typeof(IReadOnlyList<>))
        {
            throw new YtDlpValidationException(
                $"Argument '{binding.Attribute.Name}' is repeatable but property '{binding.OptionProperty.Name}' is not an IReadOnlyList<T>.");
        }

        var itemType = listType.GetGenericArguments()[0];
        var current = binding.OptionProperty.GetValue(group);
        var list = CreateMutableList(itemType, current);

        if (binding.Attribute.ValueTokenCount == 1)
        {
            list.Add(ConvertValue(values[0], itemType));
        }
        else
        {
            list.Add(values.ToArray());
        }

        binding.OptionProperty.SetValue(group, list);
    }

    private static IList CreateMutableList(Type itemType, object? current)
    {
        var concreteListType = typeof(List<>).MakeGenericType(itemType);
        var list = (IList)Activator.CreateInstance(concreteListType)!;

        if (current is IEnumerable existing)
        {
            foreach (var item in existing)
            {
                list.Add(item);
            }
        }

        return list;
    }

    private static object ConvertValue(string value, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType is not null)
        {
            targetType = nullableType;
        }

        if (targetType == typeof(string))
        {
            return value;
        }

        if (targetType == typeof(int))
        {
            return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(double))
        {
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        if (targetType.IsEnum)
        {
            return ParseEnumValue(value, targetType);
        }

        throw new YtDlpValidationException(
            $"Cannot parse value '{value}' for option property type '{targetType.Name}'.");
    }

    private static object ParseEnumValue(string value, Type enumType)
    {
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<YtDlpEnumValueAttribute>();
            if (string.Equals(attribute?.Value, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(field.Name, value, StringComparison.OrdinalIgnoreCase))
            {
                return field.GetValue(null)!;
            }
        }

        throw new YtDlpValidationException(
            $"Value '{value}' is not valid for enum '{enumType.Name}'.");
    }

    private static IReadOnlyDictionary<string, OptionBinding> BuildOptionBindings()
    {
        var bindings = new Dictionary<string, OptionBinding>(StringComparer.Ordinal);

        foreach (var groupProperty in typeof(YtDlpOptions)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Select(property => new
                     {
                         Property = property,
                         GroupAttribute = property.GetCustomAttribute<YtDlpOptionGroupAttribute>()
                     })
                     .Where(static item => item.GroupAttribute is not null)
                     .OrderBy(static item => item.GroupAttribute!.Order)
                     .ThenBy(static item => item.Property.MetadataToken))
        {
            foreach (var optionProperty in groupProperty.Property.PropertyType
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(static property => property.MetadataToken))
            {
                var attribute = optionProperty.GetCustomAttribute<YtDlpArgumentAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var binding = new OptionBinding(groupProperty.Property, optionProperty, attribute);
                AddBinding(bindings, attribute.Name, binding);
                foreach (var alias in attribute.Aliases)
                {
                    AddBinding(bindings, alias, binding);
                }
            }
        }

        return bindings;
    }

    private static void AddBinding(
        IDictionary<string, OptionBinding> bindings,
        string name,
        OptionBinding binding)
    {
        if (!bindings.ContainsKey(name))
        {
            bindings.Add(name, binding);
        }
    }

    private static IEnumerable<string> TokenizeConfigLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith(";", StringComparison.Ordinal))
        {
            yield break;
        }

        var current = new StringBuilder();
        char? quote = null;
        var escaping = false;

        foreach (var c in line)
        {
            if (escaping)
            {
                current.Append(c);
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (quote is not null)
        {
            throw new YtDlpValidationException("Config line has an unterminated quoted value.");
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string QuoteConfigToken(string token) =>
        NeedsQuoting(token)
            ? "\"" + token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : token;

    private static string QuoteCommandLineToken(string token) =>
        NeedsQuoting(token)
            ? "'" + token.Replace("'", "'\\''", StringComparison.Ordinal) + "'"
            : token;

    private static bool NeedsQuoting(string token) =>
        token.Length == 0
        || token.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '/' and not ':' and not ',' and not '@' and not '=' and not '%' and not '+');

    private sealed record OptionBinding(
        PropertyInfo GroupProperty,
        PropertyInfo OptionProperty,
        YtDlpArgumentAttribute Attribute);
}
