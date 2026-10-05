namespace DataBridge.Persistence.Queries;

internal static class PoliciesQueries
{
    public static IReadOnlyDictionary<string, ProviderQuery> Statements { get; } = new Dictionary<string, ProviderQuery>(StringComparer.Ordinal)
    {
        ["AccessPolicyExecutor.ListAsync.1"] = new(
            """
            SELECT policy_id, name, description, enabled, sync_status, sync_error, version,
                   created_at, created_by_subject, updated_at, updated_by_subject
            FROM auth.access_policies
            ORDER BY lower(name), policy_id;
            """,
            """
            SELECT policy_id, name, description, enabled, sync_status, sync_error, version,
                   created_at, created_by_subject, updated_at, updated_by_subject
            FROM auth_access_policies
            ORDER BY lower(name), policy_id;
            """),
        ["AccessPolicyExecutor.ListAsync.2"] = new(
            """
            SELECT policy_id, principal_type, principal_id
            FROM auth.access_policy_assignments
            ORDER BY principal_type, principal_id;
            """,
            """
            SELECT policy_id, principal_type, principal_id
            FROM auth_access_policy_assignments
            ORDER BY principal_type, principal_id;
            """),
        ["AccessPolicyExecutor.SaveAsync.1"] = new(
            """
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
            """,
            """
            INSERT INTO auth_access_policies
                (policy_id, name, description, enabled, sync_status, sync_error, version,
                 created_at, created_by_subject, updated_at, updated_by_subject)
            VALUES
                (@id, @name, @description, @enabled, 'pending', NULL, 1,
                 (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000), @created_by, (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000), @updated_by)
            ON CONFLICT (policy_id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                enabled = EXCLUDED.enabled,
                sync_status = 'pending',
                sync_error = NULL,
                version = auth_access_policies.version + 1,
                updated_at = (CAST(strftime('%s', 'now') AS INTEGER) * 1000000 + CAST(substr(strftime('%f', 'now'), 4, 3) AS INTEGER) * 1000),
                updated_by_subject = EXCLUDED.updated_by_subject;
            """),
        ["AccessPolicyExecutor.SaveAsync.2"] = new(
            """
            DELETE FROM auth.{fs0} WHERE policy_id = @id;
            """,
            """
            DELETE FROM auth_{fs0} WHERE policy_id = @id;
            """),
        ["AccessPolicyExecutor.SaveAsync.3"] = new(
            """
            INSERT INTO auth.access_policy_assignments (policy_id, principal_type, principal_id)
            VALUES (@id, @type, @principal);
            """,
            """
            INSERT INTO auth_access_policy_assignments (policy_id, principal_type, principal_id)
            VALUES (@id, @type, @principal);
            """),
        ["AccessPolicyExecutor.DeleteAsync.1"] = new(
            """
            DELETE FROM auth.access_policies WHERE policy_id = @id;
            """,
            """
            DELETE FROM auth_access_policies WHERE policy_id = @id;
            """),
        ["AccessPolicyExecutor.SetSyncAsync.1"] = new(
            """
            UPDATE auth.access_policies
            SET sync_status = @status, sync_error = @error
            WHERE policy_id = @id AND version = @version;
            """,
            """
            UPDATE auth_access_policies
            SET sync_status = @status, sync_error = @error
            WHERE policy_id = @id AND version = @version;
            """),
        ["AccessPolicyExecutor.ListProviderCatalogAsync.1"] = new(
            """
            SELECT DISTINCT lower(provider)
            FROM media.media_source_versions
            WHERE provider IS NOT NULL AND provider <> ''
            ORDER BY lower(provider);
            """,
            """
            SELECT DISTINCT lower(provider)
            FROM media_media_source_versions
            WHERE provider IS NOT NULL AND provider <> ''
            ORDER BY lower(provider);
            """),
        ["AccessPolicyExecutor.GetMediaSummaryAsync.1"] = new(
            """
            SELECT EXISTS(SELECT 1 FROM media.media WHERE media_guid = @media) AS found,
                   (SELECT max(title) FROM metadata.media_metadata WHERE media_guid = @media) AS title,
                   (SELECT max(age_limit) FROM metadata.media_metadata WHERE media_guid = @media) AS age_limit,
                   COALESCE((SELECT array_agg(DISTINCT lower(provider) ORDER BY lower(provider))
                             FROM media.media_source_versions
                             WHERE media_guid = @media AND provider IS NOT NULL AND provider <> ''), ARRAY[]::text[]) AS providers;
            """,
            """
            SELECT EXISTS(SELECT 1 FROM media_media WHERE media_guid = @media) AS found,
                   (SELECT max(title) FROM metadata_media_metadata WHERE media_guid = @media) AS title,
                   (SELECT max(age_limit) FROM metadata_media_metadata WHERE media_guid = @media) AS age_limit,
                   (SELECT json_group_array(provider) FROM (SELECT DISTINCT lower(provider) AS provider FROM media_media_source_versions WHERE media_guid = @media AND provider IS NOT NULL AND provider <> '' ORDER BY lower(provider))) AS providers;
            """),
        ["AccessPolicyExecutor.LoadAssignedDenyScopesAsync.1"] = new(
            """
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
            """,
            """
            WITH assigned AS (
                SELECT DISTINCT p.policy_id
                FROM auth_access_policies p
                JOIN auth_access_policy_assignments a ON a.policy_id = p.policy_id
                WHERE p.enabled
                  AND ((a.principal_type = 'user' AND a.principal_id = @subject)
                       OR (a.principal_type = 'group' AND lower(a.principal_id) IN (SELECT value FROM json_each(@groups))))
            ),
            scopes AS (
                SELECT policy_id, 'media' AS axis, fs_guid_text(media_guid) AS resource
                FROM auth_access_policy_media
                UNION ALL
                SELECT policy_id, 'provider', provider
                FROM auth_access_policy_providers
                UNION ALL
                SELECT policy_id, 'age', CAST(minimum_age AS TEXT)
                FROM auth_access_policy_age_tiers
            )
            SELECT scopes.policy_id, scopes.axis, scopes.resource
            FROM scopes
            JOIN assigned ON assigned.policy_id = scopes.policy_id
            ORDER BY scopes.policy_id, scopes.axis, scopes.resource;
            """),
        ["AccessPolicyExecutor.LoadStringsAsync.1"] = new(
            """
            SELECT policy_id, {fs0} FROM auth.{fs1} ORDER BY {fs2};
            """,
            """
            SELECT policy_id, {fs0} FROM auth_{fs1} ORDER BY {fs2};
            """),
        ["AccessPolicyExecutor.LoadGuidsAsync.1"] = new(
            """
            SELECT policy_id, {fs0} FROM auth.{fs1} ORDER BY {fs2};
            """,
            """
            SELECT policy_id, {fs0} FROM auth_{fs1} ORDER BY {fs2};
            """),
        ["AccessPolicyExecutor.LoadIntsAsync.1"] = new(
            """
            SELECT policy_id, {fs0} FROM auth.{fs1} ORDER BY {fs2};
            """,
            """
            SELECT policy_id, {fs0} FROM auth_{fs1} ORDER BY {fs2};
            """),
        ["AccessPolicyExecutor.InsertValuesAsync.1"] = new(
            """
            INSERT INTO auth.{fs0} (policy_id, {fs1}) VALUES (@id, @value);
            """,
            """
            INSERT INTO auth_{fs0} (policy_id, {fs1}) VALUES (@id, @value);
            """),
    };
}
