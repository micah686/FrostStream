# Media access Phase 3

Phase 3 adds a shared effective-access evaluator to `AccessControlController`. It combines the two
authorization axes without treating either data store as authoritative for the other:

- OpenFGA `list-objects` supplies the endpoints a principal may invoke.
- PostgreSQL-backed access policies supply media GUID, provider, and minimum-age deny scopes.

The evaluator is available through:

- `GET api/global/access-control/effective` for an administrator-selected user or group;
- `POST api/global/access-control/effective/check` for one combined endpoint/media decision with
  per-axis reasons;
- `GET api/global/access-control/effective/me` for the authenticated subject, using the current
  token's group claims for deny-policy assignment.

All three routes share validation, OpenFGA lookup, policy assignment, deny-scope union, and
provenance logic. Enabled assigned policies contribute deny scopes immediately even when their
OpenFGA synchronization is pending or failed. Endpoint policy provenance is limited to synchronized
policies because `syncStatus` describes only the endpoint-axis mirror.

The response distinguishes:

- all assigned policy IDs;
- policies contributing synchronized endpoint bundles;
- policies contributing deny scopes;
- transitional direct bundle grants;
- policy bundle grants and effective OpenFGA endpoint IDs;
- unioned media GUID, provider, and age-threshold deny scopes;
- each source policy's bundles, denies, synchronization state, and contribution axes.

The check response combines the requested endpoint decision and media decisions. It is allowed only
when every requested axis is allowed. Missing media is an explicit denied decision rather than an
empty or ambiguous result.

OpenFGA failures, policy-storage failures, and user-group lookup failures return `503` so the
administrator view cannot silently report incomplete access. User-group discovery now performs a
filtered, paginated OpenFGA tuple read for the selected user and the `member` relation instead of
scanning the full tuple store.
