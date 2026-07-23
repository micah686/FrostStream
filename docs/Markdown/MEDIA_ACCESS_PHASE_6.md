# Media access phase 6 — single-user mode and operations

Phase 6 removes the unified access-control surface when FrostStream runs in single-user mode.

- `AccessControlController` is excluded from MVC discovery, so `/api/global/access-control/*` has no registered route.
- The Administration navigation omits Access control in single-user mode.
- `AccessPolicyReconciliationService` starts only in multi-user mode, alongside the OpenFGA-backed access-policy services it reconciles.

The single-user integration test verifies that no access-control API route is registered. (The global
fallback authorization policy returns `403 Forbidden` for unmatched anonymous requests before MVC
can return `404 Not Found`.)
