using NodaTime;
using Npgsql;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class AccessPolicyExecutor(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<AccessPolicyDto>> ListAsync(CancellationToken cancellationToken)
    {
        var policies = new Dictionary<Guid, PolicyBuilder>();
        await using (var command = dataSource.CreateCommand("""
            SELECT policy_id, name, description, enabled, sync_status, sync_error, version,
                   created_at, created_by_subject, updated_at, updated_by_subject
            FROM auth.access_policies
            ORDER BY lower(name), policy_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var builder = new PolicyBuilder
                {
                    PolicyId = reader.GetGuid(reader.GetOrdinal("policy_id")),
                    Name = reader.GetString(reader.GetOrdinal("name")),
                    Description = GetNullableString(reader, "description"),
                    Enabled = reader.GetBoolean(reader.GetOrdinal("enabled")),
                    SyncStatus = ParseSyncStatus(reader.GetString(reader.GetOrdinal("sync_status"))),
                    SyncError = GetNullableString(reader, "sync_error"),
                    Version = reader.GetInt64(reader.GetOrdinal("version")),
                    CreatedAt = GetInstant(reader, "created_at"),
                    CreatedBySubject = GetNullableString(reader, "created_by_subject"),
                    UpdatedAt = GetInstant(reader, "updated_at"),
                    UpdatedBySubject = GetNullableString(reader, "updated_by_subject")
                };
                policies[builder.PolicyId] = builder;
            }
        }

        if (policies.Count == 0)
        {
            return [];
        }

        await LoadStringsAsync("access_policy_bundles", "bundle_id", policies, (p, value) => p.BundleIds.Add(value), cancellationToken);
        await LoadGuidsAsync("access_policy_media", "media_guid", policies, (p, value) => p.MediaGuids.Add(value), cancellationToken);
        await LoadStringsAsync("access_policy_providers", "provider", policies, (p, value) => p.Providers.Add(value), cancellationToken);
        await LoadIntsAsync("access_policy_age_tiers", "minimum_age", policies, (p, value) => p.AgeThresholds.Add(value), cancellationToken);

        await using (var command = dataSource.CreateCommand("""
            SELECT policy_id, principal_type, principal_id
            FROM auth.access_policy_assignments
            ORDER BY principal_type, principal_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (policies.TryGetValue(reader.GetGuid(reader.GetOrdinal("policy_id")), out var policy))
                {
                    policy.Assignments.Add(new AccessPolicyAssignmentDto
                    {
                        Type = reader.GetString(reader.GetOrdinal("principal_type")),
                        Id = reader.GetString(reader.GetOrdinal("principal_id"))
                    });
                }
            }
        }

        return policies.Values.Select(x => x.Build()).ToArray();
    }

    public async Task<AccessPolicyDto?> GetAsync(Guid policyId, CancellationToken cancellationToken)
        => (await ListAsync(cancellationToken)).FirstOrDefault(x => x.PolicyId == policyId);

    public async Task<AccessPolicyDto> SaveAsync(AccessPolicyDto policy, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            INSERT INTO auth.access_policies
                (policy_id, name, description, enabled, sync_status, sync_error, version,
                 created_at, created_by_subject, updated_at, updated_by_subject)
            VALUES
                (@id, @name, @description, @enabled, 'pending', NULL, 1,
                 CURRENT_TIMESTAMP, @created_by, CURRENT_TIMESTAMP, @updated_by)
            ON CONFLICT (policy_id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                enabled = EXCLUDED.enabled,
                sync_status = 'pending',
                sync_error = NULL,
                version = auth.access_policies.version + 1,
                updated_at = CURRENT_TIMESTAMP,
                updated_by_subject = EXCLUDED.updated_by_subject;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", policy.PolicyId);
            command.Parameters.AddWithValue("name", policy.Name);
            command.Parameters.AddWithValue("description", (object?)policy.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("enabled", policy.Enabled);
            command.Parameters.AddWithValue("created_by", (object?)policy.CreatedBySubject ?? DBNull.Value);
            command.Parameters.AddWithValue("updated_by", (object?)policy.UpdatedBySubject ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in new[]
                 {
                     "access_policy_bundles", "access_policy_media", "access_policy_providers",
                     "access_policy_age_tiers", "access_policy_assignments"
                 })
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM auth.{table} WHERE policy_id = @id;", connection, transaction);
            delete.Parameters.AddWithValue("id", policy.PolicyId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertValuesAsync(connection, transaction, "access_policy_bundles", "bundle_id", policy.PolicyId, policy.BundleIds, cancellationToken);
        await InsertValuesAsync(connection, transaction, "access_policy_media", "media_guid", policy.PolicyId, policy.MediaGuids, cancellationToken);
        await InsertValuesAsync(connection, transaction, "access_policy_providers", "provider", policy.PolicyId, policy.Providers, cancellationToken);
        await InsertValuesAsync(connection, transaction, "access_policy_age_tiers", "minimum_age", policy.PolicyId, policy.AgeThresholds, cancellationToken);

        foreach (var assignment in policy.Assignments)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO auth.access_policy_assignments (policy_id, principal_type, principal_id)
                VALUES (@id, @type, @principal);
                """, connection, transaction);
            command.Parameters.AddWithValue("id", policy.PolicyId);
            command.Parameters.AddWithValue("type", assignment.Type);
            command.Parameters.AddWithValue("principal", assignment.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(policy.PolicyId, cancellationToken)
               ?? throw new InvalidOperationException("Saved access policy could not be reloaded.");
    }

    public async Task<bool> DeleteAsync(Guid policyId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM auth.access_policies WHERE policy_id = @id;");
        command.Parameters.AddWithValue("id", policyId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<AccessPolicyDto?> SetSyncAsync(
        Guid policyId,
        long version,
        AccessPolicySyncStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE auth.access_policies
            SET sync_status = @status, sync_error = @error
            WHERE policy_id = @id AND version = @version;
            """);
        command.Parameters.AddWithValue("id", policyId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("status", status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetAsync(policyId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListProviderCatalogAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT lower(provider)
            FROM media.media_source_versions
            WHERE provider IS NOT NULL AND provider <> ''
            ORDER BY lower(provider);
            """);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    public async Task<AccessPolicyMediaSummaryDto> GetMediaSummaryAsync(Guid mediaGuid, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM media.media WHERE media_guid = @media) AS found,
                   (SELECT max(title) FROM metadata.media_metadata WHERE media_guid = @media) AS title,
                   (SELECT max(age_limit) FROM metadata.media_metadata WHERE media_guid = @media) AS age_limit,
                   COALESCE((SELECT array_agg(DISTINCT lower(provider) ORDER BY lower(provider))
                             FROM media.media_source_versions
                             WHERE media_guid = @media AND provider IS NOT NULL AND provider <> ''), ARRAY[]::text[]) AS providers;
            """);
        command.Parameters.AddWithValue("media", mediaGuid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new AccessPolicyMediaSummaryDto
        {
            MediaGuid = mediaGuid,
            Found = reader.GetBoolean(reader.GetOrdinal("found")),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? null : reader.GetString(reader.GetOrdinal("title")),
            AgeLimit = reader.IsDBNull(reader.GetOrdinal("age_limit")) ? null : reader.GetInt32(reader.GetOrdinal("age_limit")),
            Providers = reader.GetFieldValue<string[]>(reader.GetOrdinal("providers"))
        };
    }

    public async Task<AccessPolicyEffectiveMediaDto> EvaluateAsync(
        Guid mediaGuid,
        string? userSubject,
        IReadOnlyList<string> userGroups,
        IReadOnlyCollection<string> bypassGroups,
        CancellationToken cancellationToken)
    {
        var summary = await GetMediaSummaryAsync(mediaGuid, cancellationToken);
        var normalizedGroups = userGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bypass = bypassGroups.Any(normalizedGroups.Contains);
        IReadOnlyList<AccessPolicyDenyScope> scopes = bypass || !summary.Found
            ? []
            : await LoadAssignedDenyScopesAsync(userSubject, normalizedGroups, cancellationToken);
        return AccessPolicyDenyEvaluator.Evaluate(summary, scopes, bypass);
    }

    private async Task<IReadOnlyList<AccessPolicyDenyScope>> LoadAssignedDenyScopesAsync(
        string? subject,
        IReadOnlySet<string> groups,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH assigned AS (
                SELECT DISTINCT p.policy_id
                FROM auth.access_policies p
                JOIN auth.access_policy_assignments a ON a.policy_id = p.policy_id
                WHERE p.enabled
                  AND ((a.principal_type = 'user' AND a.principal_id = @subject)
                       OR (a.principal_type = 'group' AND lower(a.principal_id) = ANY(@groups)))
            ),
            scopes AS (
                SELECT policy_id, 'media'::text AS axis, media_guid::text AS resource
                FROM auth.access_policy_media
                UNION ALL
                SELECT policy_id, 'provider', provider
                FROM auth.access_policy_providers
                UNION ALL
                SELECT policy_id, 'age', minimum_age::text
                FROM auth.access_policy_age_tiers
            )
            SELECT scopes.policy_id, scopes.axis, scopes.resource
            FROM scopes
            JOIN assigned ON assigned.policy_id = scopes.policy_id
            ORDER BY scopes.policy_id, scopes.axis, scopes.resource;
            """);
        command.Parameters.AddWithValue("subject", (object?)subject ?? "");
        command.Parameters.AddWithValue(
            "groups",
            groups.Select(group => group.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray());

        var scopes = new Dictionary<Guid, DenyScopeBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!scopes.TryGetValue(id, out var scope))
            {
                scope = new DenyScopeBuilder(id);
                scopes[id] = scope;
            }

            var resource = reader.GetString(2);
            switch (reader.GetString(1))
            {
                case "media":
                    scope.MediaGuids.Add(Guid.Parse(resource));
                    break;
                case "provider":
                    scope.Providers.Add(resource);
                    break;
                case "age":
                    scope.MinimumAges.Add(int.Parse(resource));
                    break;
            }
        }

        return scopes.Values.Select(scope => scope.Build()).ToArray();
    }

    private sealed class DenyScopeBuilder(Guid policyId)
    {
        public HashSet<Guid> MediaGuids { get; } = [];
        public HashSet<string> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> MinimumAges { get; } = [];

        public AccessPolicyDenyScope Build()
            => new(policyId, MediaGuids, Providers, MinimumAges);
    }

    private async Task LoadStringsAsync(
        string table, string column, Dictionary<Guid, PolicyBuilder> policies,
        Action<PolicyBuilder, string> add, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT policy_id, {column} FROM auth.{table} ORDER BY {column};");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (policies.TryGetValue(reader.GetGuid(0), out var policy))
            {
                add(policy, reader.GetString(1));
            }
        }
    }

    private async Task LoadGuidsAsync(
        string table, string column, Dictionary<Guid, PolicyBuilder> policies,
        Action<PolicyBuilder, Guid> add, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT policy_id, {column} FROM auth.{table} ORDER BY {column};");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (policies.TryGetValue(reader.GetGuid(0), out var policy))
            {
                add(policy, reader.GetGuid(1));
            }
        }
    }

    private async Task LoadIntsAsync(
        string table, string column, Dictionary<Guid, PolicyBuilder> policies,
        Action<PolicyBuilder, int> add, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT policy_id, {column} FROM auth.{table} ORDER BY {column};");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (policies.TryGetValue(reader.GetGuid(0), out var policy))
            {
                add(policy, reader.GetInt32(1));
            }
        }
    }

    private static async Task InsertValuesAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string column,
        Guid policyId,
        IEnumerable<T> values,
        CancellationToken cancellationToken)
    {
        foreach (var value in values.Distinct())
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO auth.{table} (policy_id, {column}) VALUES (@id, @value);",
                connection, transaction);
            command.Parameters.AddWithValue("id", policyId);
            command.Parameters.AddWithValue("value", value!);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static AccessPolicySyncStatus ParseSyncStatus(string value)
        => Enum.TryParse<AccessPolicySyncStatus>(value, true, out var status)
            ? status
            : AccessPolicySyncStatus.Failed;

    private static string? GetNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Instant GetInstant(NpgsqlDataReader reader, string name)
    {
        var value = reader.GetDateTime(reader.GetOrdinal(name));
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return Instant.FromDateTimeUtc(utc);
    }

    private sealed class PolicyBuilder
    {
        public Guid PolicyId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public bool Enabled { get; init; }
        public AccessPolicySyncStatus SyncStatus { get; init; }
        public string? SyncError { get; init; }
        public long Version { get; init; }
        public List<string> BundleIds { get; } = [];
        public List<Guid> MediaGuids { get; } = [];
        public List<string> Providers { get; } = [];
        public List<int> AgeThresholds { get; } = [];
        public List<AccessPolicyAssignmentDto> Assignments { get; } = [];
        public NodaTime.Instant CreatedAt { get; init; }
        public string? CreatedBySubject { get; init; }
        public NodaTime.Instant UpdatedAt { get; init; }
        public string? UpdatedBySubject { get; init; }

        public AccessPolicyDto Build() => new()
        {
            PolicyId = PolicyId,
            Name = Name,
            Description = Description,
            Enabled = Enabled,
            SyncStatus = SyncStatus,
            SyncError = SyncError,
            Version = Version,
            BundleIds = BundleIds,
            MediaGuids = MediaGuids,
            Providers = Providers,
            AgeThresholds = AgeThresholds,
            Assignments = Assignments,
            CreatedAt = CreatedAt,
            CreatedBySubject = CreatedBySubject,
            UpdatedAt = UpdatedAt,
            UpdatedBySubject = UpdatedBySubject
        };
    }
}
