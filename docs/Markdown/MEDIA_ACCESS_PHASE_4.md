# Media access Phase 4

Phase 4 retires the legacy migration-047 media-access implementation.

The live watch and thumbnail gate now sends `AccessPolicyEffectiveMediaRequestMessage` on
`AccessPolicySubjects.EffectiveMedia`. DataBridge's `AccessPolicyConsumerService` evaluates the
request using the unified access-policy deny model introduced in Phase 1. A denied evaluation still
returns `403`; a missing, failed, or incomplete DataBridge response returns `503`, preserving the
existing fail-closed behavior.

The following legacy components are removed:

- `MediaAccessExecutor` and its direct reads and writes against the migration-047 tables;
- `MediaAccessConsumerService` and all `media-access.*` NATS subscriptions;
- `MediaAccessMessages.cs`, including the legacy management and watch-check contracts;
- DataBridge dependency-injection and hosted-service registrations for those components;
- the obsolete `media-access-admin` endpoint bundle constant;
- allow-list-specific executor unit tests.

The old controllers and endpoint IDs were already removed during Phase 2. Migration 068 remains
responsible for dropping `auth.media_access_restrictions`, `auth.provider_access_restrictions`, and
`auth.age_limit_policies` without converting their allow-list rows into deny policies.

The `MediaAccess` configuration section and `MediaAccessOptions` remain active because the unified
access-policy evaluator uses `AdminBypassGroups` for the watch-path bypass rule.
