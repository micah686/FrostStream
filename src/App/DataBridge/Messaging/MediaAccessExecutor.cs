using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Evaluates and administers watch-time media access control. Three independent layers are checked at
/// watch time (all must pass): a per-media group allow-list, a per-provider group allow-list, and a
/// tiered age-limit policy. Members of a configured bypass group skip all three.
///
/// Access state lives entirely in the <c>auth.*</c> tables; the caller's group membership is supplied
/// from their Authentik claims by the WebAPI, so no token handling happens here.
/// </summary>
public sealed class MediaAccessExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string[] _bypassGroups;
    private readonly ILogger<MediaAccessExecutor> _logger;

    public MediaAccessExecutor(
        NpgsqlDataSource dataSource,
        IOptions<MediaAccessOptions> options,
        ILogger<MediaAccessExecutor> logger)
    {
        _dataSource = dataSource;
        _bypassGroups = options.Value.AdminBypassGroups ?? [];
        _logger = logger;
    }

    // --- Watch-time gate ---------------------------------------------------

    public async Task<MediaAccessCheckResponseMessage> EvaluateAsync(
        Guid mediaGuid,
        IReadOnlyList<string> userGroups,
        CancellationToken cancellationToken = default)
    {
        var groups = new HashSet<string>(userGroups, StringComparer.OrdinalIgnoreCase);

        if (Intersects(_bypassGroups, groups))
        {
            return Allowed();
        }

        // 1. Per-media restriction.
        var mediaGroups = await LoadMediaGroupsAsync(mediaGuid, cancellationToken);
        if (mediaGroups.Count > 0 && !Intersects(mediaGroups, groups))
        {
            return Denied("media-restricted");
        }

        // 2. Provider restriction. Only providers that appear in the restriction table are gated.
        var providers = await LoadMediaProvidersAsync(mediaGuid, cancellationToken);
        if (providers.Count > 0)
        {
            var providerRules = await LoadProviderRulesAsync(providers, cancellationToken);
            foreach (var (provider, allowed) in providerRules)
            {
                if (!Intersects(allowed, groups))
                {
                    return Denied($"provider-restricted:{provider}");
                }
            }
        }

        // 3. Age-limit policy. Only the highest tier at or below the media's age_limit applies.
        var ageLimit = await LoadMediaAgeLimitAsync(mediaGuid, cancellationToken);
        if (ageLimit is > 0)
        {
            var tierGroups = await LoadHighestAgeTierGroupsAsync(ageLimit.Value, cancellationToken);
            if (tierGroups.Count > 0 && !Intersects(tierGroups, groups))
            {
                return Denied("age-restricted");
            }
        }

        return Allowed();
    }

    // --- Per-media administration -----------------------------------------

    public async Task<IReadOnlyList<string>> ListMediaGroupsAsync(Guid mediaGuid, CancellationToken cancellationToken)
        => await LoadMediaGroupsListAsync(mediaGuid, cancellationToken);

    public async Task AddMediaGroupAsync(Guid mediaGuid, string groupName, string? subject, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO auth.media_access_restrictions (id, media_guid, group_name, created_by_subject)
            VALUES (@id, @media, @group, @subject)
            ON CONFLICT (media_guid, group_name) DO NOTHING;
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("media", mediaGuid);
        command.Parameters.AddWithValue("group", groupName);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveMediaGroupAsync(Guid mediaGuid, string groupName, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM auth.media_access_restrictions WHERE media_guid = @media AND group_name = @group;");
        command.Parameters.AddWithValue("media", mediaGuid);
        command.Parameters.AddWithValue("group", groupName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearMediaGroupsAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM auth.media_access_restrictions WHERE media_guid = @media;");
        command.Parameters.AddWithValue("media", mediaGuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Provider administration ------------------------------------------

    public async Task<IReadOnlyList<MediaAccessProviderEntryDto>> ListProvidersAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT provider_pattern, group_name FROM auth.provider_access_restrictions ORDER BY provider_pattern, group_name;");

        var byProvider = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var provider = reader.GetString(0);
            if (!byProvider.TryGetValue(provider, out var list))
            {
                list = [];
                byProvider[provider] = list;
            }

            list.Add(reader.GetString(1));
        }

        return byProvider
            .Select(kvp => new MediaAccessProviderEntryDto { Provider = kvp.Key, Groups = kvp.Value })
            .ToArray();
    }

    public async Task AddProviderGroupAsync(string provider, string groupName, string? subject, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO auth.provider_access_restrictions (provider_pattern, group_name, created_by_subject)
            VALUES (@provider, @group, @subject)
            ON CONFLICT (provider_pattern, group_name) DO NOTHING;
            """);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("group", groupName);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveProviderGroupAsync(string provider, string groupName, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM auth.provider_access_restrictions WHERE provider_pattern = @provider AND group_name = @group;");
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("group", groupName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearProviderAsync(string provider, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM auth.provider_access_restrictions WHERE provider_pattern = @provider;");
        command.Parameters.AddWithValue("provider", provider);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Age-limit policy administration ----------------------------------

    public async Task<IReadOnlyList<MediaAccessAgePolicyDto>> ListAgePoliciesAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT minimum_age_limit, group_name FROM auth.age_limit_policies ORDER BY minimum_age_limit, group_name;");

        var byTier = new SortedDictionary<int, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tier = reader.GetInt32(0);
            if (!byTier.TryGetValue(tier, out var list))
            {
                list = [];
                byTier[tier] = list;
            }

            list.Add(reader.GetString(1));
        }

        return byTier
            .Select(kvp => new MediaAccessAgePolicyDto { Threshold = kvp.Key, Groups = kvp.Value })
            .ToArray();
    }

    public async Task AddAgePolicyAsync(int threshold, string groupName, string? subject, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO auth.age_limit_policies (id, minimum_age_limit, group_name, created_by_subject)
            VALUES (@id, @threshold, @group, @subject)
            ON CONFLICT (minimum_age_limit, group_name) DO NOTHING;
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("threshold", threshold);
        command.Parameters.AddWithValue("group", groupName);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAgePolicyAsync(int threshold, string groupName, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "DELETE FROM auth.age_limit_policies WHERE minimum_age_limit = @threshold AND group_name = @group;");
        command.Parameters.AddWithValue("threshold", threshold);
        command.Parameters.AddWithValue("group", groupName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Pure helpers (unit-tested) ---------------------------------------

    /// <summary>True when any allowed group matches one of the user's groups (case-insensitive).</summary>
    public static bool Intersects(IEnumerable<string> allowedGroups, IReadOnlySet<string> userGroups)
    {
        foreach (var allowed in allowedGroups)
        {
            if (userGroups.Contains(allowed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Highest configured tier at or below <paramref name="ageLimit"/>, or null when none apply.</summary>
    public static int? SelectHighestApplicableTier(IEnumerable<int> tiers, int ageLimit)
    {
        int? best = null;
        foreach (var tier in tiers)
        {
            if (tier <= ageLimit && (best is null || tier > best))
            {
                best = tier;
            }
        }

        return best;
    }

    // --- Read queries ------------------------------------------------------

    private async Task<List<string>> LoadMediaGroupsAsync(Guid mediaGuid, CancellationToken cancellationToken)
        => await LoadMediaGroupsListAsync(mediaGuid, cancellationToken);

    private async Task<List<string>> LoadMediaGroupsListAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT group_name FROM auth.media_access_restrictions WHERE media_guid = @media ORDER BY group_name;");
        command.Parameters.AddWithValue("media", mediaGuid);

        var groups = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(reader.GetString(0));
        }

        return groups;
    }

    private async Task<List<string>> LoadMediaProvidersAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT DISTINCT lower(provider) AS provider
            FROM media.media_source_versions
            WHERE media_guid = @media AND provider IS NOT NULL AND provider <> '';
            """);
        command.Parameters.AddWithValue("media", mediaGuid);

        var providers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(reader.GetString(0));
        }

        return providers;
    }

    private async Task<Dictionary<string, List<string>>> LoadProviderRulesAsync(
        IReadOnlyList<string> providers,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT provider_pattern, group_name FROM auth.provider_access_restrictions WHERE provider_pattern = ANY(@providers);");
        command.Parameters.AddWithValue("providers", providers.ToArray());

        var rules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var provider = reader.GetString(0);
            if (!rules.TryGetValue(provider, out var list))
            {
                list = [];
                rules[provider] = list;
            }

            list.Add(reader.GetString(1));
        }

        return rules;
    }

    private async Task<int?> LoadMediaAgeLimitAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT max(age_limit) FROM metadata.media_metadata WHERE media_guid = @media;");
        command.Parameters.AddWithValue("media", mediaGuid);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private async Task<List<string>> LoadHighestAgeTierGroupsAsync(int ageLimit, CancellationToken cancellationToken)
    {
        // The highest tier at or below the media's age limit wins; only its groups gate access.
        await using var command = _dataSource.CreateCommand("""
            SELECT group_name
            FROM auth.age_limit_policies
            WHERE minimum_age_limit = (
                SELECT max(minimum_age_limit)
                FROM auth.age_limit_policies
                WHERE minimum_age_limit <= @age
            );
            """);
        command.Parameters.AddWithValue("age", ageLimit);

        var groups = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(reader.GetString(0));
        }

        return groups;
    }

    private static MediaAccessCheckResponseMessage Allowed()
        => new() { IsAllowed = true };

    private MediaAccessCheckResponseMessage Denied(string reason)
    {
        _logger.LogDebug("Media access denied: {Reason}.", reason);
        return new MediaAccessCheckResponseMessage { IsAllowed = false, FailureReason = reason };
    }
}
