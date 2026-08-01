using System.Collections;
using System.Globalization;
using System.Reflection;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Options;

namespace YtDlpSharpLib.Rendering;

/// <summary>
/// Reflection-driven renderer that converts <see cref="YtDlpOptions"/> into
/// deterministic argv tokens.
/// </summary>
public sealed class YtDlpArgumentRenderer : IYtDlpArgumentRenderer
{
    /// <inheritdoc />
    public IReadOnlyList<string> Render(YtDlpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>();

        foreach (var group in GetOptionGroups(options))
        {
            RenderGroup(args, group);
        }

        RenderAdvanced(args, options.AdvancedArguments);

        return args;
    }

    private static IEnumerable<object> GetOptionGroups(YtDlpOptions options) =>
        options.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => new
            {
                Property = property,
                Group = property.GetCustomAttribute<YtDlpOptionGroupAttribute>()
            })
            .Where(item => item.Group is not null)
            .OrderBy(item => item.Group!.Order)
            .ThenBy(item => item.Property.MetadataToken)
            .Select(item => item.Property.GetValue(options))
            .Where(static value => value is not null)
            .Select(static value => value!);

    private static void RenderGroup(List<string> args, object group)
    {
        foreach (var property in group.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(static property => property.MetadataToken))
        {
            var attribute = property.GetCustomAttribute<YtDlpArgumentAttribute>();
            if (attribute is null)
            {
                continue;
            }

            var value = property.GetValue(group);
            if (value is null)
            {
                continue;
            }

            if (attribute.ValueStyle == ArgumentValueStyle.Switch)
            {
                if (value is true)
                {
                    args.Add(attribute.Name);
                }

                continue;
            }

            if (attribute.ValueTokenCount > 1)
            {
                RenderMultiValue(args, attribute, value);
                continue;
            }

            if (value is IEnumerable sequence and not string)
            {
                foreach (var item in sequence)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    args.Add(attribute.Name);
                    args.Add(RenderValue(item));
                }

                continue;
            }

            args.Add(attribute.Name);
            args.Add(RenderValue(value));
        }
    }

    private static void RenderMultiValue(List<string> args, YtDlpArgumentAttribute attribute, object value)
    {
        if (attribute.AllowMultiple)
        {
            if (value is string || value is not IEnumerable rows)
            {
                throw new YtDlpValidationException(
                    $"Argument '{attribute.Name}' expects a sequence of value token sequences.");
            }

            foreach (var row in rows)
            {
                if (row is null)
                {
                    continue;
                }

                RenderMultiValueRow(args, attribute, row);
            }

            return;
        }

        RenderMultiValueRow(args, attribute, value);
    }

    private static void RenderMultiValueRow(List<string> args, YtDlpArgumentAttribute attribute, object value)
    {
        if (value is string || value is not IEnumerable sequence)
        {
            throw new YtDlpValidationException(
                $"Argument '{attribute.Name}' expects {attribute.ValueTokenCount} value tokens.");
        }

        var values = sequence.Cast<object?>().Where(static item => item is not null).ToArray();
        if (values.Length != attribute.ValueTokenCount)
        {
            throw new YtDlpValidationException(
                $"Argument '{attribute.Name}' expects {attribute.ValueTokenCount} value tokens, but got {values.Length}.");
        }

        args.Add(attribute.Name);
        foreach (var item in values)
        {
            args.Add(RenderValue(item!));
        }
    }

    private static void RenderAdvanced(List<string> args, IReadOnlyList<RawYtDlpArgument> advancedArguments)
    {
        foreach (var argument in advancedArguments)
        {
            if (!argument.Name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new YtDlpValidationException(
                    $"Advanced argument '{argument.Name}' must be a long yt-dlp flag (begin with '--').");
            }

            args.Add(argument.Name);

            if (argument.Value is not null)
            {
                args.Add(argument.Value);
            }

            foreach (var value in argument.Values)
            {
                args.Add(value);
            }
        }
    }

    private static string RenderValue(object value) =>
        value switch
        {
            string text => text,
            Enum otherEnum => RenderAttributedEnum(otherEnum),
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new YtDlpValidationException($"Could not render value '{value}'.")
        };

    private static string RenderAttributedEnum(Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        var attribute = field?.GetCustomAttribute<YtDlpEnumValueAttribute>();

        return attribute?.Value ?? value.ToString().ToLowerInvariant();
    }
}
