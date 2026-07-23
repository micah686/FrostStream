# Media access Phase 2

Phase 2 consolidates access administration under `api/global/access-control`.

The unified controller now owns:

- endpoint catalog and Authentik directory lookup;
- system/custom bundle list, detail, creation, endpoint replacement, and deletion;
- bundle policy-membership queries and per-bundle endpoint/policy counts;
- policy CRUD, duplication, impact preview, provider catalog, media summary, and the existing effective-access query.

Custom bundle creation accepts `cloneFrom` for a code-defined system baseline. The `all` lock-out
guard and custom bundles cannot be clone sources. Callers may also provide an `endpoints` collection
when composing a bundle from scratch or extending the cloned baseline.

Bundle deletion returns `409 Conflict` with the blocking policies while any policy references the
bundle. If policy storage cannot be checked, deletion fails closed with `503`.

The old `api/global/management`, `api/global/access-management`, and `api/global/media-access`
controller routes and endpoint IDs are retired. All replacement endpoint IDs use the
`access-control.*` prefix and remain in the `management` bundle.

Policy writes preserve deferred synchronization: if PostgreSQL succeeds but the OpenFGA mirror
fails, the API returns `202 Accepted` with `syncStatus: Failed`; the reconciliation service retries
the mirror.
