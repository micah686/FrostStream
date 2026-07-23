# Media access phase 7 — OpenFGA cleanup and hardening

Phase 7 reduces OpenFGA model churn and bounds tuple reads to the access-control data each
operation needs.

## Authorization model lifecycle

`WebAPI/Auth/OpenFgaModel.json` is the canonical authorization model and is embedded into the
WebAPI assembly at build time. The former DSL copy and in-code JSON literal were removed, so model
changes have one source.

During auto-provisioning, WebAPI:

1. Computes a canonical SHA-256 hash of the embedded model, ignoring server-assigned metadata and
   OpenFGA's read-time empty/default fields.
2. Pages through the store's immutable authorization models.
3. Reuses the newest model with the same content hash.
4. Writes a new model only when no matching version exists.

An explicitly configured authorization-model ID remains authoritative and bypasses discovery.

## Filtered tuple reads

Bundle listing reads each code-defined endpoint's exact membership tuples (with bounded
parallelism), then reads grants only for the discovered capability groups. OpenFGA requires an
exact object or user for `Read`; type-only object filters are rejected. Policy synchronization and
removal read only the target policy's incoming grantee tuples and outgoing bundle-policy tuples.
Pagination remains enabled for every filtered query.

This removes full-store tuple scans from both `OpenFgaBundleManagementService` and
`OpenFgaAccessPolicyService`.
