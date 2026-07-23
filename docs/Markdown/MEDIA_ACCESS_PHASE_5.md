# Media access Phase 5

Phase 5 consolidates access administration into one frontend route:
`/admin/access-control`.

The administration navigation now exposes **Access control**, whose page contains three views:

- **Policies** manages endpoint-bundle grants and media GUID, provider, and age-tier denies. Bundle
  selection comes from the unified bundle catalog. Media GUIDs must resolve through
  `GET api/global/access-control/media/{guid}` before they can be added, providers use the provider
  catalog as autocomplete suggestions, and principal assignments use Authentik directory search.
- **Bundles** shows system and runtime bundles, endpoint membership, and every referencing policy.
  Runtime bundle creation can start from a code-defined system bundle through `cloneFrom`; the
  copied baseline is identified separately from additional selected endpoints.
- **Effective access** uses directory search to select a user or group, displays effective bundles,
  OpenFGA endpoint IDs, source policies, and unioned deny scopes, and invokes
  `POST api/global/access-control/effective/check` for endpoint and media checks. The result table
  shows the allow/deny status and reason for each returned axis.

The previous `/admin/access-management`, `/admin/bundle-management`, and `/admin/media-access`
routes redirect to `/admin/access-control`. Their old Svelte pages, the migration-047
`MediaAccessSection`, and the `/api/global/media-access` frontend client are removed.
