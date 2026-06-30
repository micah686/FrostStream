using System.Text.Json;

namespace Shared.Database;

public sealed record CreatorSourceProviderQueryLimits
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Dictionary<string, CreatorSourceTypeQueryLimits> Providers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int? GetLimit(string platform, CreatorSourceType sourceType)
    {
        if (string.IsNullOrWhiteSpace(platform) || Providers.Count == 0)
        {
            return null;
        }

        var provider = Providers.GetValueOrDefault(platform.Trim())
            ?? Providers.FirstOrDefault(x => string.Equals(x.Key, platform.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
        return provider?.GetLimit(sourceType);
    }

    public IReadOnlyList<string> Validate(int maxLimit)
    {
        var errors = new List<string>();
        foreach (var (provider, limits) in Providers)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                errors.Add("provider query limit keys must be non-empty provider names.");
                continue;
            }

            if (limits is null)
            {
                errors.Add($"provider query limits for '{provider}' must be an object.");
                continue;
            }

            foreach (var (name, value) in limits.AllLimits())
            {
                if (value is null)
                {
                    continue;
                }

                if (value <= 0)
                {
                    errors.Add($"provider query limit '{provider}.{name}' must be greater than zero.");
                }
                else if (value > maxLimit)
                {
                    errors.Add($"provider query limit '{provider}.{name}' must be less than or equal to {maxLimit}.");
                }
            }
        }

        return errors;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static CreatorSourceProviderQueryLimits? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CreatorSourceProviderQueryLimits>(json, JsonOptions);
    }
}

public sealed record CreatorSourceTypeQueryLimits
{
    public int? Videos { get; init; }
    public int? Shorts { get; init; }
    public int? Streams { get; init; }
    public int? Playlist { get; init; }
    public int? Clips { get; init; }
    public int? Vods { get; init; }

    public int? GetLimit(CreatorSourceType sourceType)
        => sourceType switch
        {
            CreatorSourceType.Videos => Videos,
            CreatorSourceType.Shorts => Shorts,
            CreatorSourceType.Streams => Streams,
            CreatorSourceType.Playlist => Playlist,
            CreatorSourceType.Clips => Clips,
            CreatorSourceType.Vods => Vods,
            _ => null
        };

    public IEnumerable<(string Name, int? Value)> AllLimits()
    {
        yield return (nameof(Videos), Videos);
        yield return (nameof(Shorts), Shorts);
        yield return (nameof(Streams), Streams);
        yield return (nameof(Playlist), Playlist);
        yield return (nameof(Clips), Clips);
        yield return (nameof(Vods), Vods);
    }
}
