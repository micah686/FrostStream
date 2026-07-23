# Media access deny-semantics migration

Phase 1 of `MEDIA_ACCESS.MD` changes unified policy media scopes to deny semantics:

- endpoint bundles remain positive grants;
- assigned media GUIDs, providers, and minimum ages deny playback;
- media without a matching assigned deny remains watchable;
- age thresholds are inclusive, and unrated media is not denied by age;
- media denies apply from PostgreSQL regardless of OpenFGA synchronization status.

Migration 068 permanently drops the legacy migration-047 allow-list tables and their data. Those
rows are intentionally not converted: treating a former list of principals allowed to watch as a
list of principals denied from watching would invert its security meaning. Administrators must
recreate any desired restrictions as deny scopes in unified access policies.

Migration 068 also removes deterministic policy rows created by the earlier uncommitted form of
migration 067, if that migration was already exercised in a development database. This prevents
those copied allow rules from changing meaning after the upgrade.
